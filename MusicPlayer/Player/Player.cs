using Microsoft.Extensions.Logging;
using MusicPlayer.Common;
using MusicPlayer.FileManagement;
using MusicPlayer.Helpers;
using MusicPlayer.LibraryManagement;
using MusicPlayer.LibraryManagement.Models;
using MusicPlayer.Player.Models;
using MusicPlayer.PlaylistManagement.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicPlayer.Player
{
    public sealed class Player : IDisposable
    {
        private const string ASIO_DRIVER = "Focusrite USB ASIO";

        // The Corsair HS80 has no ASIO driver of its own (USB gaming headsets never do - only pro audio
        // interfaces like the Focusrite ship one), so it's reached via WasapiOut instead, matched against the
        // Windows device name. A substring match on "HS80" rather than the full "Speakers (CORSAIR HS80 RGB USB
        // Gaming Headset)" string is deliberately loose - resilient to Windows renaming/reordering the exact
        // "Speakers (...)" prefix, while still specific enough not to collide with anything else plugged in.
        private const string HEADSET_DEVICE_NAME_SUBSTRING = "HS80";

        private static readonly ILogger _logger = AppLogger.CreateLogger<Player>();

        private readonly string _activeDriver;
        private AudioOutput _activeOutput;
        private PlaybackMode _activeMode;
        private IWavePlayer _audioPlayer;
        private DynamicPlaylistSampleProvider _playlistProvider;
        private List<PlaylistTrack> _queuedPlaylist;
        private float[] _bandCenterFrequencies;

        private bool _isInitialized;
        private readonly object _locker = new object();
        private bool disposedValue;

        private PlaybackInformation _playbackInformation;
        public PlaybackInformation PlaybackInformation { get => GetPlaybackInformation(); }

        #region Singleton
        private static readonly Lazy<Player> _instance = new Lazy<Player>(() => new Player());
        public static Player Instance { get => _instance.Value; }

        public List<EqualizerPreset> EqualizerPresets { get; private set; }

        private Player()
        {
            var drivers = AsioOut.GetDriverNames();
            _logger.LogInformation("Available ASIO drivers: {Drivers}", drivers.Any() ? string.Join(", ", drivers) : "(none found)");

            _activeDriver = drivers.FirstOrDefault(driver => string.Equals(driver, ASIO_DRIVER));

            if (_activeDriver != null)
            {
                _logger.LogInformation("Using ASIO driver {ActiveDriver}", _activeDriver);
            }
            else
            {
                // Previously this silently passed null into `new AsioOut(_activeDriver)` in Play() if
                // ASIO_DRIVER wasn't found — which fails deep inside ASIO COM interop with a much less useful
                // error than a clear log line up front. Falling back to whatever's first mirrors what the
                // parameterless AsioOut() constructor does, so playback still has the best chance of working,
                // but now it's visible in the logs which driver actually ended up in use instead of being a
                // guessing game (e.g. ASIO4ALL vs. a native Focusrite driver).
                _activeDriver = drivers.FirstOrDefault();
                _logger.LogWarning("Configured ASIO driver \"{ConfiguredDriver}\" not found; falling back to {FallbackDriver}", ASIO_DRIVER, _activeDriver ?? "(none available)");
            }

            (_activeOutput, _activeMode) = LoadPlayerSettingsAsync().GetAwaiter().GetResult();
            _logger.LogInformation("Starting with audio output {AudioOutput}, playback mode {PlaybackMode}", _activeOutput, _activeMode);

            PrepareEqualizerPresets();
        }

        #endregion Singleton

        public void InitializePlayer()
        {
            if (_isInitialized) return;

            _queuedPlaylist = new List<PlaylistTrack>();
            _playbackInformation = new PlaybackInformation();
            ApplyPersistedSettingsToPlayerState();
            _isInitialized = true;
            LibraryManager.Instance.UpdatedFavouriteTrack += HandleUpdatedFavouriteTrack;
        }

        public void Play(string[] paths)
        {
            if (!_isInitialized) InitializePlayer();

            _playbackInformation = new PlaybackInformation();
            ApplyPersistedSettingsToPlayerState();
            _queuedPlaylist = new List<PlaylistTrack>(paths.Select(LibraryManager.Instance.GetTrackInformation));

            var trackConfigurations = _queuedPlaylist.ToDictionary(track => track.Path, Track => Track.EqualizerPreset);

            _playlistProvider = new DynamicPlaylistSampleProvider(trackConfigurations, _bandCenterFrequencies);
            _playlistProvider.ReachedEndOfProvider += HandleReachedEndOfTrack;
            _playlistProvider.ReachedEndOfPlaylist += HandleReachedEndOfPlaylist;
            _playlistProvider.QueuedProvidersAdded += HandleQueuedProvidersAdded;

            if (_audioPlayer != null)
            {
                _audioPlayer.Stop();
                _audioPlayer.Dispose();
                _audioPlayer = null;
            }

            _audioPlayer = CreateAudioPlayer();
            _audioPlayer.Init(_playlistProvider);
            _audioPlayer.Play();
        }

        public void Pause() => _audioPlayer.Pause();

        public void Resume() => _audioPlayer.Play();

        // Called from PlayerHub on the same STA thread every other player action runs on - both AsioOut and
        // WasapiOut are COM-based underneath and expect it. Deliberately doesn't rebuild _playlistProvider the way
        // Play() does: NAudio sample providers track their own read position internally, so reusing the same
        // instance and only swapping the IWavePlayer underneath it means switching speakers <-> headset mid-track
        // resumes from exactly where it was instead of restarting the current track.
        public void SetAudioOutput(AudioOutput output)
        {
            if (_activeOutput == output) return;

            lock (_locker)
            {
                _activeOutput = output;

                if (_audioPlayer != null && _playlistProvider != null)
                {
                    var wasPlaying = _audioPlayer.PlaybackState == PlaybackState.Playing;

                    _audioPlayer.Stop();
                    _audioPlayer.Dispose();
                    _audioPlayer = CreateAudioPlayer();
                    _audioPlayer.Init(_playlistProvider);

                    if (wasPlaying) _audioPlayer.Play();
                }

                // PlaybackInformation's getter kicks off UpdatePlaybackInformation on a background thread and
                // returns immediately (see GetPlaybackInformation) - PlayerHub.SetAudioOutputAsync broadcasts right
                // after this call returns specifically so a switch while paused/stopped still reaches the UI (the
                // 500ms PlaybackBroadcast loop only runs while something is playing), so PlayerState.AudioOutput
                // needs to already be correct by then rather than depending on that background refresh's timing.
                if (_playbackInformation != null)
                    _playbackInformation.PlayerState.AudioOutput = _activeOutput;
            }

            _ = Task.Run(async () => await SavePlayerSettingsAsync());
        }

        // Speakers go through the same ASIO driver Play() has always used. Headset has no ASIO driver, so it goes
        // through WasapiOut targeting whichever active render-endpoint's name contains "HS80". If that device
        // isn't found (headset unplugged, Windows renamed it, etc.), falls back to speakers with a warning rather
        // than throwing - same fallback philosophy as the ASIO driver selection in the constructor.
        private IWavePlayer CreateAudioPlayer()
        {
            if (_activeOutput == AudioOutput.Headset)
            {
                // Classic using(){} block rather than a C# 8 using declaration - MusicPlayer targets C# 7.3 (see
                // the same note on library mapping's Parallel.ForEachAsync avoidance), which doesn't support it.
                MMDevice headsetDevice;
                using (var enumerator = new MMDeviceEnumerator())
                {
                    headsetDevice = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                        .FirstOrDefault(device => device.FriendlyName?.IndexOf(HEADSET_DEVICE_NAME_SUBSTRING, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (headsetDevice != null)
                {
                    _logger.LogInformation("Using WASAPI headset device {DeviceName}", headsetDevice.FriendlyName);
                    return new WasapiOut(headsetDevice, AudioClientShareMode.Shared, true, 200);
                }

                _logger.LogWarning("No active audio device matching \"{Substring}\" found; falling back to speakers", HEADSET_DEVICE_NAME_SUBSTRING);
                _activeOutput = AudioOutput.Speakers;
            }

            return new AsioOut(_activeDriver);
        }

        // _playbackInformation gets replaced wholesale (`new PlaybackInformation()`) in both InitializePlayer()
        // and Play(), which resets PlayerState back to its class defaults (Speakers/Sequential) regardless of
        // what's actually persisted/active. Previously the only thing that corrected this was
        // UpdatePlaybackInformation()'s background task - not guaranteed to have run yet by the time a client
        // asks for a fresh snapshot right after connecting/refreshing, so a reload could briefly (or not so
        // briefly) show the wrong output/mode even though playback itself was already using the right one.
        // Calling this immediately after each reset makes the broadcast correct from the very first snapshot.
        private void ApplyPersistedSettingsToPlayerState()
        {
            _playbackInformation.PlayerState.AudioOutput = _activeOutput;
            _playbackInformation.PlayerState.Mode = _activeMode;
        }

        private async Task<(AudioOutput AudioOutput, PlaybackMode Mode)> LoadPlayerSettingsAsync()
        {
            try
            {
                var settings = await MongoDbClient.Instance.GetPlayerSettingsAsync();
                return (settings?.AudioOutput ?? AudioOutput.Speakers, settings?.Mode ?? PlaybackMode.Sequential);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to load player settings; defaulting to speakers/sequential");
                return (AudioOutput.Speakers, PlaybackMode.Sequential);
            }
        }

        private async Task SavePlayerSettingsAsync()
        {
            try
            {
                await MongoDbClient.Instance.SavePlayerSettingsAsync(new PlayerSettings { AudioOutput = _activeOutput, Mode = _activeMode });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to save player settings");
            }
        }

        public void PlayNext()
        {
            _audioPlayer.Stop();
            _playlistProvider.PlayNextProvider();
            _audioPlayer.Play();
        }

        public void PlayPrevious()
        {
            _audioPlayer.Stop();
            _playlistProvider.PlayPreviousProvider();
            _audioPlayer.Play();
        }

        public void TogglePlaybackMode()
        {
            switch (_playbackInformation.PlayerState.Mode)
            {
                case PlaybackMode.Sequential:
                    _playbackInformation.PlayerState.Mode = PlaybackMode.RepeatOne;
                    break;
                case PlaybackMode.RepeatOne:
                    _playbackInformation.PlayerState.Mode = PlaybackMode.RepeatAll;
                    break;
                case PlaybackMode.RepeatAll:
                    _playbackInformation.PlayerState.Mode = PlaybackMode.Shuffle;
                    break;
                case PlaybackMode.Shuffle:
                default:
                    _playbackInformation.PlayerState.Mode = PlaybackMode.Sequential;
                    break;
            }

            _activeMode = _playbackInformation.PlayerState.Mode;
            _ = Task.Run(async () => await SavePlayerSettingsAsync());
        }

        public void AddToNowPlaying(string[] paths, bool canAppend, string indexPath = null)
        {
            Task.Run(() =>
            {
                var trackInfos = new List<PlaylistTrack>(paths.Select(LibraryManager.Instance.GetTrackInformation));
                var trackConfigs = trackInfos.ToDictionary(info => info.Path, info => info.EqualizerPreset);
                _playlistProvider.AddProviders(trackConfigs, canAppend, indexPath);
                var queuePathsOrder = _playlistProvider.Providers.Select(provider => (provider as EnhancedAudioFileReader).OriginalFilePath).ToList();
                _queuedPlaylist.AddRange(trackInfos);
                _queuedPlaylist = _queuedPlaylist.OrderBy(track => queuePathsOrder.IndexOf(track.Path)).ToList();
            });
        }

        public void ReorderNowPlayingPlaylist(string[] paths)
        {
            _playlistProvider.ReOrderProviders(paths);
            _queuedPlaylist = _queuedPlaylist.OrderBy(track => paths.ToList().IndexOf(track.Path)).ToList();
        }

        public void RemoveNowPlayingTrack(string[] paths)
        {
            _playlistProvider.RemoveProvider(paths);
            _queuedPlaylist.RemoveAll(track => paths.Contains(track.Path));
        }

        public List<PlaylistTrack> GetNowPlayingPlaylist() => _queuedPlaylist;

        private PlaybackInformation GetPlaybackInformation()
        {
            Task.Run(UpdatePlaybackInformation);
            return _playbackInformation;
        }

        private void UpdatePlaybackInformation()
        {
            lock (_locker)
            {
                try
                {
                    if (!_isInitialized) InitializePlayer();

                    if (_playlistProvider == null || _queuedPlaylist == null) return;

                    if (_playlistProvider.Providers.Count() != _queuedPlaylist.Count) return;

                    foreach (var trackInfo in _queuedPlaylist)
                    {
                        var queuedTrack = _playlistProvider.Providers.First(track => string.Equals((track as EnhancedAudioFileReader).OriginalFilePath, trackInfo.Path));
                        var queuedTrackFile = queuedTrack as EnhancedAudioFileReader;
                        var currentTrackFile = _playlistProvider?.CurrentProvider as EnhancedAudioFileReader;

                        trackInfo.TotalDuration = queuedTrackFile == null ? 0 : queuedTrackFile?.TotalTime.TotalSeconds ?? 0;
                        trackInfo.IsPlaying = string.Equals(trackInfo.Path, (_playlistProvider?.CurrentProvider as EnhancedAudioFileReader)?.OriginalFilePath);

                        if (string.Equals(currentTrackFile?.OriginalFilePath, trackInfo.Path))
                            trackInfo.PlayedDuration = currentTrackFile.CurrentTime.TotalSeconds;
                        else
                            trackInfo.PlayedDuration = queuedTrackFile.CurrentTime.TotalSeconds;
                    }

                    _playbackInformation.PlayerState.IsPlaying = _audioPlayer?.PlaybackState == PlaybackState.Playing;
                    _playbackInformation.PlayerState.HasPrevious = _playlistProvider?.HasPreviousProvider ?? false;
                    _playbackInformation.PlayerState.HasNext = (_playlistProvider?.HasNextProvider) ?? false;
                    _playbackInformation.PlayerState.AudioOutput = _activeOutput;
                    _playbackInformation.Tracks = _queuedPlaylist;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to update playback information");
                }
            }
        }

        // Raised (after a 2s debounce) once the playlist genuinely finishes, so MusicServer can stop its playback
        // broadcast loop. MusicPlayer can't reference MusicServer's PlaybackBroadcast class directly (would be
        // circular), so this uses the same plain-event bridge as MappingUpdate.Changed and AppLogger — MusicServer
        // subscribes once at startup in PlaybackBroadcast.Initialize. Replaces the old self-HTTP-POST back into
        // BroadcastController's own StopPlaybackInformation endpoint.
        public event EventHandler PlaybackBroadcastStopRequested;

        public void HandleReachedEndOfPlaylist(object sender, ProviderPathsEventArgs e)
        {
            _playbackInformation.HasReachedEndOfPlaylist = true;
            _ = Task.Run(() => new Debouncer().Debounce(ResetPlaylistElementsAsync, 2000));
            _ = Task.Run(async () => await new Debouncer().DebounceAsync(token => RaisePlaybackBroadcastStopRequested(), 2000));
            _ = Task.Run(() => new Debouncer().Debounce(() => Parallel.ForEach(e.Providers, provider => FileManager.Instance.RemoveFile((provider as EnhancedAudioFileReader).ServerFilepath)), 5000));
        }

        public void HandleReachedEndOfTrack(object sender, CurrentProviderEventArgs e)
        {
            if (System.Diagnostics.Debugger.IsAttached) return;
            Task.Run(async () => await LibraryManager.Instance.UpdateTrackPlayedAsync((e.Provider as EnhancedAudioFileReader).OriginalFilePath));
        }

        private void ResetPlaylistElementsAsync()
        {
            _audioPlayer = null;
            _playlistProvider.Dispose();
            _playlistProvider = null;
            _queuedPlaylist = null;
        }

        private Task RaisePlaybackBroadcastStopRequested()
        {
            PlaybackBroadcastStopRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        private void HandleQueuedProvidersAdded(object sender, EventArgs e)
        {
            //_queuedPlaylist.Clear();
        }

        private void HandleUpdatedFavouriteTrack(object sender, FavouriteTrackEventArgs e)
        {
            var queuedTrack = _queuedPlaylist.FirstOrDefault(track => string.Equals(track.Path, e.FilePath));
            if (queuedTrack == null) return;
            queuedTrack.IsFavourite = e.IsFavourite;
        }

        public void ApplyEqualizerPreset(EqualizerPreset preset)
        {
            _playlistProvider.SetPresetFrequencyBandGains(preset.FrequencyBands.ToArray());
            if (string.Equals(preset.Name, "Bypass")) return;
            Task.Run(async () => await SaveEqualizerPresetAsync(preset));
        }

        private void PrepareEqualizerPresets()
        {
            EqualizerPresets = Task.Run(async () => await MongoDbClient.Instance.GetEqualizerPresetsAsync()).GetAwaiter().GetResult();
            _bandCenterFrequencies = EqualizerPresets.FirstOrDefault(preset => string.Equals(preset.Name, "Flat"))?.FrequencyBands.OrderBy(band => band.Frequency).Select(band => band.Frequency).ToArray();

            if (EqualizerPresets.Any()) return;

            var configPath = Path.Combine(Constants.LIBRARY_DIRECTORY, "config.json");
            var jsonString = FileManager.Instance.Read(configPath);
            var rootObject = JObject.Parse(jsonString);
            var presetsToken = rootObject["equalizerPresets"];
            EqualizerPresets = presetsToken.ToObject<List<EqualizerPreset>>();

            Parallel.ForEach(EqualizerPresets, async preset => await MongoDbClient.Instance.InsertEqualizerPresetAsync(preset));
        }

        private async Task SaveEqualizerPresetAsync(EqualizerPreset preset)
        {
            try
            {
                var trackPath = (_playlistProvider.CurrentProvider as EnhancedAudioFileReader).OriginalFilePath;
                var existingPreset = await MongoDbClient.Instance.GetEqualizerPresetAsync(trackPath);

                if (existingPreset != null)
                {
                    existingPreset.FrequencyBands = preset.FrequencyBands;
                    await MongoDbClient.Instance.UpdateEqualizerPresetAsync(existingPreset);
                    return;
                }

                var albumPath = Path.GetDirectoryName(trackPath);
                var album = await MongoDbClient.Instance.GetAlbumAsync(albumPath);
                var track = album.Tracks.Find(filteredTrack => string.Equals(filteredTrack.Path, trackPath));

                preset.Id = MongoDB.Bson.ObjectId.Empty;
                preset.Guid = Guid.NewGuid();
                preset.Name = trackPath;
                preset.IsDefault = false;
                await MongoDbClient.Instance.InsertEqualizerPresetAsync(preset);

                track.EqualizerGuid = preset.Guid;
                await MongoDbClient.Instance.UpdateAlbumAsync(album);

                var albumFile = FileManager.Instance.GetFilePaths(Constants.JSON_FILE_PATTERN, Constants.GUUID_FILE_PATTERN).FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file), album.Guid.ToString()));
                var albumFileContent = FileManager.Instance.Read(albumFile);
                var cachedAlbum = JsonConvert.DeserializeObject<Album>(albumFileContent, settings: JsonSerializationHelper.FileSerializerSettings);
                var cachedTrack = cachedAlbum.Tracks.Find(filteredTrack => string.Equals(filteredTrack.Path, trackPath));
                cachedTrack.EqualizerGuid = track.EqualizerGuid;
                FileManager.Instance.Write(cachedAlbum);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to save equalizer preset");
            }
        }

        public List<EqualizerPreset> GetMergedEqualizerPresets()
        {
            var trackPreset = (_playlistProvider.CurrentProvider as EnhancedAudioFileReader).EqualizerPreset;

            if (trackPreset == null)
                return EqualizerPresets;

            trackPreset.Name = "Saved";
            return EqualizerPresets.ToList().Prepend(trackPreset).ToList();
        }

        #region Dispose Pattern
        private void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _audioPlayer?.Stop();
                    _audioPlayer?.Dispose();
                    _playlistProvider?.Dispose();
                    _queuedPlaylist = null;
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion Dispose Pattern
    }
}
