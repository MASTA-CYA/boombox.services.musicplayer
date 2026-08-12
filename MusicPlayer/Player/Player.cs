using Microsoft.Extensions.Logging;
using MusicPlayer.Common;
using MusicPlayer.FileManagement;
using MusicPlayer.Helpers;
using MusicPlayer.LibraryManagement;
using MusicPlayer.LibraryManagement.Models;
using MusicPlayer.Player.Models;
using MusicPlayer.PlaylistManagement.Models;
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

        private static readonly ILogger _logger = AppLogger.CreateLogger<Player>();

        private readonly string _activeDriver;
        private AsioOut _audioPlayer;
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

            PrepareEqualizerPresets();
        }

        #endregion Singleton

        public void InitializePlayer()
        {
            if (_isInitialized) return;

            _queuedPlaylist = new List<PlaylistTrack>();
            _playbackInformation = new PlaybackInformation();
            _isInitialized = true;
            LibraryManager.Instance.UpdatedFavouriteTrack += HandleUpdatedFavouriteTrack;
        }

        public void Play(string[] paths)
        {
            if (!_isInitialized) InitializePlayer();

            _playbackInformation = new PlaybackInformation();
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

            _audioPlayer = new AsioOut(_activeDriver);
            _audioPlayer.Init(_playlistProvider);
            _audioPlayer.Play();
        }

        public void Pause() => _audioPlayer.Pause();

        public void Resume() => _audioPlayer.Play();

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
