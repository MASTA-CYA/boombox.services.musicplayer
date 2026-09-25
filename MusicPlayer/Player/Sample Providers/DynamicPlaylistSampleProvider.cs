using Microsoft.Extensions.Logging;
using MusicPlayer.Common;
using MusicPlayer.Extensions;
using MusicPlayer.FileManagement;
using MusicPlayer.Player.Models;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace MusicPlayer.Player
{
    public class DynamicPlaylistSampleProvider : ISampleProvider, IDisposable
    {
        private const int SAMPLE_RATE = 44100;
        private const float BAND_WIDTH_Q = 1.414f;

        // How many tracks ahead of / behind the current one stay resampled to disk at any given time. Everything
        // else in the queue exists only as a cheap "unsampled" reader (opens the original source file directly,
        // just to know its duration - no resample, no disk write) until the rolling window reaches it. This is
        // what actually caps disk usage for a long queue: resampling used to happen eagerly for the WHOLE queue
        // the moment it was played/queued, regardless of how much of it you'd actually get to. See KNOWN_ISSUES.md
        // for the full write-up (lazy resample window feature).
        private const int LOOKAHEAD_WINDOW = 2;
        private const int RETENTION_WINDOW = 1;

        private static readonly ILogger _logger = AppLogger.CreateLogger<DynamicPlaylistSampleProvider>();

        private PeekingEnumerator<ISampleProvider> _enumerator;
        private IEnumerable<QueuedProviderInstruction> _queuedProviders;
        private List<string> _proposedOrder;
        private List<string> _removedProviders;
        private bool disposedValue;

        private IEnumerable<ISampleProvider> _providers;
        public IEnumerable<ISampleProvider> Providers { get => GetProviders(); }
        private ISampleProvider _currentProvider;

        // Background resample results waiting to be swapped into _providers at the next safe track boundary -
        // same reasoning as _queuedProviders: Read() runs on the audio callback thread mid-track, so _providers
        // can't be mutated from a background Task directly. Populated from Task.Run closures (any thread),
        // drained only from EnsureWindowResampled (always the audio thread) - ConcurrentQueue is what makes that
        // cross-thread handoff safe without an explicit lock.
        private readonly ConcurrentQueue<EnhancedAudioFileReader> _resampledSwapsReady = new ConcurrentQueue<EnhancedAudioFileReader>();

        // Tracks which original paths currently have a background resample in flight, so a track that's been in
        // the lookahead window for two consecutive boundary checks doesn't get a second redundant resample task
        // kicked off before the first one finishes. Added to from the audio thread (EnsureWindowResampled), but
        // removed from both there AND from inside the Task.Run closure's catch block (a background thread) on
        // failure - a plain HashSet isn't safe for that cross-thread mutation, hence ConcurrentDictionary used
        // as a set (ignore the value, only the key matters).
        private readonly ConcurrentDictionary<string, byte> _resamplingInFlight = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // Shuffle mode used to re-randomize the entire remaining queue on every single track-end. With lazy
        // resampling that no longer works - only ~3-4 tracks ever have a live provider at once, so reshuffling
        // just that tiny window wouldn't be a meaningful shuffle across a large playlist, and deciding "what's
        // next" only at the exact moment the current track ends makes a resample-ahead impossible to fit in
        // before it's needed. Instead the order is decided once, the first time shuffle mode is seen, and walked
        // sequentially from then on through the same lazy window as every other mode. Reset to false whenever the
        // mode isn't Shuffle (see HandleEndOfProviderReached), so re-entering shuffle later gets a fresh shuffle.
        private bool _shuffleOrderEstablished;

        private readonly float[] _bandCenterFrequencies;
        private readonly BiQuadFilter[,] _equalizerFrequencyFilters;
        private readonly float[] _activeFrequencyBandGains;

        public ISampleProvider CurrentProvider { get => _currentProvider; }
        public bool HasPreviousProvider { get => HasPreviuosProvider(); }
        // Was `_enumerator.HasNext || _queuedProviders.Any()` - _enumerator wraps the raw, not-yet-purged
        // _providers, so a track that had just been deleted (removal is deferred to AddQueuedProviders at the
        // next track change, same as reorder) still counted as "next" here until that purge actually ran. That
        // let the UI's Next button stay enabled after deleting the only remaining upcoming track, so clicking
        // it triggered a real "reached end of playlist" instead of just doing nothing / greying out. Basing
        // this on GetProviders() (which already accounts for both pending removals and pending adds) instead of
        // the raw enumerator makes it correct immediately, without waiting for the deferred purge.
        public bool HasNextProvider { get => HasEffectiveNextProvider(); }
        public WaveFormat WaveFormat { get; }

        public event EventHandler<CurrentProviderEventArgs> ReachedEndOfProvider;
        public event EventHandler<ProviderPathsEventArgs> ReachedEndOfPlaylist;
        public event EventHandler QueuedProvidersAdded;

        public DynamicPlaylistSampleProvider(Dictionary<string, EqualizerPreset> trackConfigurations, float[] frequencies)
        {
            // Only the current track plus the next LOOKAHEAD_WINDOW get a real resample + disk cache write here -
            // everything else in the queue is built as a cheap "unsampled" reader (opens the original source file
            // directly, just for its duration - see GetUnsampledProviders) and gets upgraded later by
            // EnsureWindowResampled as the rolling window reaches it. _providers still holds the FULL queue from
            // the start, in order - Player.cs's polling (UpdatePlaybackInformation) depends on Providers.Count()
            // matching the full display list and every track having a valid reader to read TotalTime/CurrentTime
            // off, both of which an unsampled reader already satisfies.
            var orderedConfigs = trackConfigurations.ToList();
            var windowConfigs = orderedConfigs.Take(LOOKAHEAD_WINDOW + 1).ToDictionary(kv => kv.Key, kv => kv.Value);
            var remainder = orderedConfigs.Skip(LOOKAHEAD_WINDOW + 1);

            var initialProviders = GetResampledProviders(windowConfigs).ToList();
            initialProviders.AddRange(GetUnsampledProviders(remainder.ToDictionary(kv => kv.Key, kv => kv.Value)));

            _providers = initialProviders;
            _enumerator = new PeekingEnumerator<ISampleProvider>(_providers);
            _currentProvider = _enumerator.Current;

            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SAMPLE_RATE, 2);
            _queuedProviders = new List<QueuedProviderInstruction>();
            _proposedOrder = new List<string>();
            _removedProviders = new List<string>();

            _bandCenterFrequencies = frequencies;
            _activeFrequencyBandGains = (_currentProvider as EnhancedAudioFileReader).EqualizerPreset?.FrequencyBands?.Select(band => band.Gain).ToArray() ?? new float[9];
            _equalizerFrequencyFilters = new BiQuadFilter[9, WaveFormat.Channels];
            CreateEqualizerFrequencyFilters();
        }

        private void CreateEqualizerFrequencyFilters()
        {
            int sampleRate = WaveFormat.SampleRate;
            int channels = WaveFormat.Channels;

            for (int band = 0; band < 9; band++)
                for (int ch = 0; ch < channels; ch++)
                    // Create a Peaking EQ filter for every frequency band and channel
                    _equalizerFrequencyFilters[band, ch] = BiQuadFilter.PeakingEQ(sampleRate, _bandCenterFrequencies[band], BAND_WIDTH_Q, _activeFrequencyBandGains[band]);
        }

        public void SetFrequencyBandGain(int bandIndex, float gainDb)
        {
            if (bandIndex < 0 || bandIndex >= 9) return;

            _activeFrequencyBandGains[bandIndex] = gainDb;

            for (int ch = 0; ch < WaveFormat.Channels; ch++)
                _equalizerFrequencyFilters[bandIndex, ch].SetPeakingEq(WaveFormat.SampleRate, _bandCenterFrequencies[bandIndex], BAND_WIDTH_Q, _activeFrequencyBandGains[bandIndex]);
        }

        public void SetPresetFrequencyBandGains(EqualizerFrequencyBand[] bands = null)
        {
            try
            {
                var orderedBandGains = new float[_bandCenterFrequencies.Length]; // "Flat" fallback = zero gain on every band

                if (bands != null)
                    orderedBandGains = bands.OrderBy(band => band.Frequency).Select(orderedBand => orderedBand.Gain).ToArray();

                if (orderedBandGains.Any(band => band > 15 || band < -15))
                    throw new InvalidDataException("Band gain exceeded limits");

                for (int i = 0; i < orderedBandGains.Length; i++)
                    SetFrequencyBandGain(i, orderedBandGains[i]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to set preset frequency band gains");
            }


        }

        // Cheap, immediate, no disk I/O beyond opening each file to read its own header (for TotalTime) - reads
        // straight from the original source, never the resampled cache. This is what every queue entry outside
        // the lookahead/retention window looks like: a real, valid EnhancedAudioFileReader (so Player.cs's
        // polling can still read OriginalFilePath/TotalTime/EqualizerPreset off it), just not yet upgraded to a
        // resampled one. Never used as _currentProvider while still in this state - EnsureWindowResampled always
        // upgrades a track before playback can reach it (see the lookahead margin).
        private IEnumerable<ISampleProvider> GetUnsampledProviders(Dictionary<string, EqualizerPreset> trackConfigurations)
            => trackConfigurations.Keys.Select(file => new EnhancedAudioFileReader(serverFileName: null, originalFileName: file, equalizerPreset: trackConfigurations[file]));

        private IEnumerable<ISampleProvider> GetResampledProviders(Dictionary<string, EqualizerPreset> trackConfigurations)
        {
            var resampledProviders = new ConcurrentBag<EnhancedAudioFileReader>();
            var resampledProvidersFilePaths = FileManager.Instance.GetResampledProviderFileNames();
            Parallel.ForEach(trackConfigurations.ToList(), config =>
            {
                var existingServerFileName = resampledProvidersFilePaths.FirstOrDefault(serverFilePath => HasMatchingServerFileName(serverFilePath, config.Key));
                resampledProviders.Add(WriteResampledFile(config.Key, config.Value, existingServerFileName));
            });
            return resampledProviders.OrderBy(provider => trackConfigurations.Keys.ToList().IndexOf(provider.OriginalFilePath));
        }

        // Resamples (or reuses an existing cached resample of) a single track and returns the ready-to-play
        // reader. Called both from GetResampledProviders (the initial window, resampled synchronously before
        // playback starts - same as the old eager-everything behavior, just scoped to a handful of tracks
        // instead of the whole queue) and from EnsureWindowResampled's background Task.Run calls (one track at a
        // time, off the audio thread, as the rolling window advances).
        private EnhancedAudioFileReader WriteResampledFile(string originalPath, EqualizerPreset preset, string existingServerFileName = null)
        {
            if (!string.IsNullOrWhiteSpace(existingServerFileName))
            {
                _logger.LogInformation("Reusing cached resample for {OriginalPath}: {ServerFileName}", originalPath, existingServerFileName);
                return new EnhancedAudioFileReader(serverFileName: existingServerFileName, originalFileName: originalPath, equalizerPreset: preset);
            }

            using (var reader = new AudioFileReader(originalPath))
            {
                // Diagnostic for the "some tracks play at ~2x speed" report (KNOWN_ISSUES.md #22) - kept in place
                // since it's cheap and still useful signal if a similarly-shaped bug ever recurs for a track that
                // only gets resampled once it's actually reached by the lazy window.
                _logger.LogInformation(
                    "Resampling {OriginalPath}: source {SourceSampleRate}Hz/{SourceChannels}ch/{SourceBitsPerSample}bit ({SourceEncoding}) -> target {TargetSampleRate}Hz",
                    originalPath, reader.WaveFormat.SampleRate, reader.WaveFormat.Channels, reader.WaveFormat.BitsPerSample, reader.WaveFormat.Encoding, SAMPLE_RATE);

                var resampler = new WdlResamplingSampleProvider(reader, SAMPLE_RATE);

                // WdlResamplingSampleProvider only changes sample rate - it keeps the source's channel count
                // as-is. Every cached WAV previously stayed mono for a mono source, but
                // DynamicPlaylistSampleProvider.WaveFormat is hardcoded to 2 channels, and Read() treats the
                // underlying stream as interleaved stereo regardless (KNOWN_ISSUES.md #22). Upmixing here, once,
                // at cache-write time, means the cached file itself is always properly stereo.
                ISampleProvider outputProvider = resampler;
                if (resampler.WaveFormat.Channels == 1)
                    outputProvider = new MonoToStereoSampleProvider(resampler);

                var resampledFilename = Path.Combine(new string[] { Constants.RESAMPLED_PROVIDERS_DIRECTORY, GetCacheFileName(originalPath) });

                // ToWaveProvider() keeps the resampler's output as 32-bit IEEE float, so the cache preserves full
                // precision; only the sample rate is intentionally changed here, not bit depth.
                WaveFileWriter.CreateWaveFile(resampledFilename, outputProvider.ToWaveProvider());
                return new EnhancedAudioFileReader(serverFileName: resampledFilename, originalFileName: originalPath, equalizerPreset: preset);
            }
        }

        // Cache filename is a short hash of the FULL original path, not just its base filename, so two different
        // tracks that happen to share a base filename can never collide on disk or in the lookup below.
        private static string GetCacheFileName(string originalPath)
        {
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(originalPath));
                var hash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);
                return $"{Path.GetFileNameWithoutExtension(originalPath)}_{hash}.wav";
            }
        }

        private bool HasMatchingServerFileName(string serverPath, string originalPath)
            => string.Equals(Path.GetFileName(serverPath), GetCacheFileName(originalPath), StringComparison.OrdinalIgnoreCase);

        public void PlayNextProvider()
        {
            (_currentProvider as EnhancedAudioFileReader).Reset();
            HandleEndOfProviderReached();
        }

        public void PlayPreviousProvider()
        {
            var currentTime = (_currentProvider as EnhancedAudioFileReader).CurrentTime.TotalSeconds;
            (_currentProvider as EnhancedAudioFileReader).Reset();

            if (currentTime > 10) return;

            _enumerator.ResetToPreviousElement();
            _currentProvider = _enumerator.Current;

            // Moving backward shifts the window too - the track that's now RETENTION_WINDOW behind the new
            // current might not be resampled (it could have been evicted a couple of tracks ago). Doesn't go
            // through HandleEndOfProviderReached (this is a direct user-triggered jump, not a track boundary), so
            // it needs its own call to keep the "always resampled within range" invariant intact.
            EnsureWindowResampled();
        }

        // Returns whether the add actually landed in _queuedProviders/_providers - KNOWN_ISSUES.md #23's "still
        // open" note: this method already swallows every exception itself (logs and returns), so previously
        // Player.AddToNowPlaying had no way to know an add had silently failed and always mutated its own display
        // list (_queuedPlaylist) regardless, permanently desyncing it from the real engine queue on any future
        // failure in here (not just the one confirmed trigger already fixed). The caller is now expected to skip
        // its own list mutation when this returns false.
        public bool AddProviders(Dictionary<string, EqualizerPreset> configs, bool canAppend = true, string indexPath = null)
        {
            try
            {
                var queue = _queuedProviders.ToList();

                // New queue entries are normally built unsampled here - cheap, immediate, no disk write. Whether
                // and when any of them actually get resampled is entirely EnsureWindowResampled's call, based on
                // where they land relative to current once they're merged into _providers. Previously this
                // resampled everything immediately regardless of position, which is exactly the eager-upfront
                // behavior that made a long queue expensive in disk space.
                //
                // The one exception: EnsureWindowResampled only ever runs at a track boundary or after
                // PlayPreviousProvider - nothing calls it just from adding to the queue. If there's currently no
                // next track at all (we're on the last one), a newly added track gets no lookahead margin: it
                // would sit unsampled until the current track actually finishes, at which point
                // EnsureWindowResampled's defensive fallback resamples it synchronously right at that boundary -
                // a real, audible pause instead of a seamless transition, since the whole point of the lookahead
                // window is to have finished well before it's needed. This method already isn't running on the
                // audio thread (called via Player.AddToNowPlaying/PlayerHub), so giving just the first new track
                // a head start here - resampled immediately, synchronously, before it's ever queued - means it's
                // ready long before the current track ends instead of causing a gap right at the boundary.
                var orderedConfigs = configs.ToList();
                var newProviders = new List<ISampleProvider>();

                if (!HasEffectiveNextProvider() && orderedConfigs.Any())
                {
                    var first = orderedConfigs[0];
                    var resampledProvidersFilePaths = FileManager.Instance.GetResampledProviderFileNames();
                    var existingServerFileName = resampledProvidersFilePaths.FirstOrDefault(serverFilePath => HasMatchingServerFileName(serverFilePath, first.Key));
                    newProviders.Add(WriteResampledFile(first.Key, first.Value, existingServerFileName));
                    orderedConfigs.RemoveAt(0);
                }

                newProviders.AddRange(GetUnsampledProviders(orderedConfigs.ToDictionary(kv => kv.Key, kv => kv.Value)));

                if (!queue.Any())
                {
                    queue.Add(new QueuedProviderInstruction
                    {
                        Providers = newProviders,
                        CanAppend = canAppend,
                        IndexPath = indexPath,
                    });
                    _queuedProviders = queue;
                    return true;
                }

                if (string.IsNullOrWhiteSpace(indexPath))
                {
                    // Looking for an existing pending instruction that is ITSELF anchored at the true end of the
                    // queue (CanAppend, and its own IndexPath is null/whitespace - that's what "append to end"
                    // means for a QueuedProviderInstruction). This used to compare against
                    // (_providers.Last() as EnhancedAudioFileReader).OriginalFilePath instead - a real file path -
                    // which an append-to-end instruction's IndexPath (always null) can never equal, so Find always
                    // returned null the moment a second "add to end of queue" call landed while a first one was
                    // still pending (i.e. before the next track boundary merged it into _providers via
                    // AddQueuedProviders). The null result then NullReferenceException'd on .Providers below,
                    // silently caught by the catch block, so the second add appeared to just do nothing - visible
                    // as "refuses to add tracks" (e.g. adding more Singles tracks shortly after adding some). Also
                    // made null-safe: if genuinely nothing is pending yet, fall through to starting a new
                    // instruction instead of crashing.
                    var lastAppendingQueuedProvider = queue.Find(provider => provider.CanAppend && string.IsNullOrWhiteSpace(provider.IndexPath));

                    if (lastAppendingQueuedProvider != null)
                    {
                        var currentProviders = lastAppendingQueuedProvider.Providers.ToList();
                        currentProviders.AddRange(newProviders);
                        lastAppendingQueuedProvider.Providers = currentProviders;
                        return true;
                    }

                    queue.Add(new QueuedProviderInstruction
                    {
                        Providers = newProviders,
                        CanAppend = canAppend,
                        IndexPath = indexPath,
                    });
                    _queuedProviders = queue;
                    return true;
                }

                foreach (var queuedProvider in queue.ToList())
                {
                    var providers = queuedProvider.Providers.ToList();
                    var index = providers.FindIndex(provider => string.Equals((provider as EnhancedAudioFileReader).OriginalFilePath, indexPath));

                    if (index != -1)
                    {
                        if (canAppend)
                            providers.AddRange(index, newProviders);
                        else
                            providers.InsertRange(index, newProviders);

                        queuedProvider.Providers = providers;
                        var amendedInstruction = queue.FirstOrDefault(instruction => string.Equals(instruction.IndexPath, queuedProvider.IndexPath));
                        amendedInstruction = queuedProvider;
                        _queuedProviders = queue;
                        return true;
                    }
                }

                queue.Add(new QueuedProviderInstruction
                {
                    Providers = newProviders,
                    CanAppend = canAppend,
                    IndexPath = indexPath,
                });
                _queuedProviders = queue;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to add providers");
                return false;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int totalRead = 0;
            int channels = WaveFormat.Channels;

            while (totalRead < count)
            {
                if (_currentProvider == null) break;

                int currentOffset = offset + totalRead;
                int remainingCount = count - totalRead;
                int samplesRead = _currentProvider.Read(buffer, currentOffset, remainingCount);

                if (samplesRead == 0)
                {
                    HandleEndOfProviderReached();
                    continue;
                }

                for (int n = 0; n < samplesRead; n++)
                {
                    int globalIndex = currentOffset + n;
                    int channel = globalIndex % channels;

                    for (int band = 0; band < 9; band++)
                        buffer[globalIndex] = _equalizerFrequencyFilters[band, channel].Transform(buffer[globalIndex]);
                }

                totalRead += samplesRead;
            }

            return totalRead;
        }

        private void HandleEndOfProviderReached()
        {
            var finishedProvider = _currentProvider;

            // Shuffle's "decide the order once" state (see the field comment above) only makes sense while
            // actually in Shuffle mode - reset it here so switching away and back later reshuffles fresh instead
            // of silently resuming whatever order was left over from the last time.
            if (Player.Instance.PlaybackInformation.PlayerState.Mode != PlaybackMode.Shuffle)
                _shuffleOrderEstablished = false;

            try
            {
                AddQueuedProviders();

                switch (Player.Instance.PlaybackInformation.PlayerState.Mode)
                {
                    case PlaybackMode.Shuffle:
                        HandleShufflePlayback();
                        break;
                    case PlaybackMode.RepeatAll:
                        HandleRepeatAllPlayback();
                        break;
                    case PlaybackMode.RepeatOne:
                        HandleRepeatOnePlayback();
                        break;
                    case PlaybackMode.Sequential:
                    default:
                        HandleSequentialPlayback();
                        break;
                }

                // Now that _currentProvider reflects wherever playback actually landed, top up the resampled
                // window around it and evict whatever's fallen out of range. Deliberately after the mode-switch,
                // not before - evicting/resampling relative to the OLD current would be off by one position
                // relative to what's actually playing next.
                EnsureWindowResampled();

                // Null-guarded: HandleSequentialPlayback sets _currentProvider to null when the playlist has
                // genuinely ended (no next track), and control still falls through to here. The old unguarded
                // `(_currentProvider as EnhancedAudioFileReader).EqualizerPreset` threw a NullReferenceException on
                // every single natural end-of-playlist — this is the exact repeating crash that used to show up in
                // Logs/error.log before Serilog made it visible.
                SetPresetFrequencyBandGains((_currentProvider as EnhancedAudioFileReader)?.EqualizerPreset?.FrequencyBands?.ToArray());
            }
            catch (Exception ex)
            {
                // This method runs synchronously on the ASIO audio callback thread — Read() needs _currentProvider
                // updated before it continues its loop (that's what makes gapless playback across tracks work
                // within a single Read() call), so none of this can simply be pushed onto a background thread.
                // An unhandled exception here previously propagated straight out through Read() into NAudio's
                // callback, which is a much worse failure mode than a graceful stop. Fail safe instead: log it and
                // clear _currentProvider — Read() already treats a null current provider as "nothing left to
                // play" and returns 0 samples, which AsioOut handles cleanly.
                _logger.LogError(ex, "Unable to advance to next provider after track ended");
                _currentProvider = null;
            }

            // Not required for Read()'s next iteration — this only feeds a fire-and-forget "times played" database
            // update in Player.HandleReachedEndOfTrack — so it's dispatched off the audio thread instead of
            // invoked inline like the rest of this method has to be.
            if (finishedProvider != null)
                Task.Run(() => ReachedEndOfProvider?.Invoke(this, new CurrentProviderEventArgs(finishedProvider)));
        }

        private void HandleSequentialPlayback()
        {
            if (!_enumerator.HasNext)
            {
                _currentProvider = null;
                var providers = _enumerator.Source.ToList();
                var currentProviderIndex = providers.IndexOf(_enumerator.Current);
                var readProviders = providers.Take(currentProviderIndex + 1);
                ReachedEndOfPlaylist?.Invoke(this, new ProviderPathsEventArgs(readProviders));
                return;
            }

            _enumerator.MoveNext();
            _currentProvider = _enumerator.Current;
        }

        private void HandleRepeatAllPlayback()
        {
            if (_enumerator.HasNext)
            {
                HandleSequentialPlayback();
                return;
            }

            _currentProvider = null;
            foreach (var provider in _providers)
                (provider as EnhancedAudioFileReader).Reset();

            _enumerator.Reset();
            _currentProvider = _enumerator.Current;
        }

        private void HandleRepeatOnePlayback()
        {
            if ((_currentProvider as EnhancedAudioFileReader).CanSeek)
            {
                (_currentProvider as EnhancedAudioFileReader).Reset();
                return;
            }

            HandleSequentialPlayback();
        }

        private void HandleShufflePlayback()
        {
            (_currentProvider as EnhancedAudioFileReader).Reset();

            if (!_shuffleOrderEstablished)
            {
                // Only the not-yet-resampled remainder of the queue gets shuffled here, not whatever's already
                // sitting resampled ahead of current in _providers - that head start was already committed to
                // disk before shuffle was toggled on, and undoing it would mean nothing is immediately ready to
                // advance onto right at this track boundary (a real "no next track" gap, since a fresh resample
                // can't finish instantly). Practically: the track immediately after enabling shuffle plays in
                // whatever order it was already queued for; everything from the one after that onward follows
                // the fresh shuffle. EnsureWindowResampled (called right after this method returns, from
                // HandleEndOfProviderReached) walks the now-shuffled remainder through the same lazy window as
                // every other mode, so the shuffle stays genuinely seamless once established.
                var providers = _providers.ToList();
                var currentIndex = providers.FindIndex(provider => provider.Equals(_currentProvider));
                var alreadyResampledAhead = currentIndex >= 0 ? providers.Skip(currentIndex + 1).ToList() : new List<ISampleProvider>();
                var stillUnsampledAhead = alreadyResampledAhead.Where(provider => (provider as EnhancedAudioFileReader).ServerFilepath == null).ToList();
                var resampledLookahead = alreadyResampledAhead.Except(stillUnsampledAhead).ToList();

                stillUnsampledAhead.Shuffle();

                // Rebuild _providers with the (untouched, already-resampled) lookahead entries first, in their
                // existing order, followed by the freshly shuffled remainder - this is the same
                // mutate-then-ResetEnumerator pattern AddQueuedProviders already uses elsewhere in this class.
                var rebuilt = new List<ISampleProvider>();
                if (currentIndex > 0) rebuilt.AddRange(providers.Take(currentIndex));
                rebuilt.Add(_currentProvider);
                rebuilt.AddRange(resampledLookahead);
                rebuilt.AddRange(stillUnsampledAhead);

                _providers = rebuilt;
                ResetEnumerator();

                _shuffleOrderEstablished = true;
            }

            HandleSequentialPlayback();
        }

        // Add/remove/reorder are three independent deferred operations, all deliberately held back from
        // touching _providers/_enumerator until here (track-end, via HandleEndOfProviderReached) rather than
        // applied immediately - this runs off the audio callback thread mid-Read(), and mutating the
        // enumerator's source while it's being read from is unsafe.
        //
        // Previously the removal branch only ran when _queuedProviders had a pending add queued too - a plain
        // delete with nothing queued to add left _removedProviders populated but never actually applied to
        // _providers. Providers.Count() then permanently disagreed with the display list's count, which tripped
        // Player.UpdatePlaybackInformation()'s guard forever (not just until the next track change) - freezing
        // every playback field (IsPlaying, HasNext, PlayedDuration, etc.) the moment a track was deleted. Each
        // branch below now runs independently, so a plain removal is applied on its own.
        private void AddQueuedProviders()
        {
            var hasRemovals = _removedProviders.Any();
            var hasQueuedAdds = _queuedProviders.Any();
            var hasReorder = _proposedOrder.Any();

            if (!hasRemovals && !hasQueuedAdds && !hasReorder) return;

            var providers = _providers.ToList();

            if (hasRemovals)
            {
                providers.RemoveAll(provider => _removedProviders.Contains((provider as EnhancedAudioFileReader).OriginalFilePath));
                _removedProviders.Clear();
            }

            if (hasQueuedAdds)
            {
                foreach (var queuedProvider in _queuedProviders)
                {
                    var index = ResolveAnchorIndex(providers, queuedProvider.IndexPath);

                    if (queuedProvider.CanAppend)
                        providers.AddRange(index, queuedProvider.Providers);
                    else
                        providers.InsertRange(index, queuedProvider.Providers);
                }

                var queue = _queuedProviders.ToList();
                queue.Clear();
                _queuedProviders = queue;
            }

            if (hasReorder)
            {
                providers = providers.OrderBy(provider => _proposedOrder.IndexOf((provider as EnhancedAudioFileReader).OriginalFilePath)).ToList();
                _proposedOrder.Clear();
            }

            _providers = providers.AsEnumerable();
            ResetEnumerator();

            if (hasQueuedAdds)
                QueuedProvidersAdded.Invoke(this, EventArgs.Empty);
        }

        // Tops up the resampled lookahead window and evicts anything that's fallen out of the retention window
        // behind current. Called at every track boundary (HandleEndOfProviderReached, right after the mode-switch
        // advance) and after a manual PlayPreviousProvider jump, since both change what "ahead"/"behind" means.
        //
        // Nothing here ever mutates _providers from a background thread - resampling itself always happens off
        // the audio thread via Task.Run, but a completed resample only gets swapped into _providers the next time
        // this method runs (via _resampledSwapsReady), on the audio thread, the same safe point
        // AddQueuedProviders already mutates _providers from. By the time a track is actually needed, its
        // resample was kicked off LOOKAHEAD_WINDOW tracks earlier, so in the overwhelmingly common case it's long
        // finished before playback ever reaches it - that's what makes the transition seamless, not any wait at
        // the boundary itself.
        private void EnsureWindowResampled()
        {
            var providers = _providers.ToList();

            // Drain any background resamples that finished since the last check and splice them in at the same
            // position the unsampled placeholder they're replacing was at. A track can finish resampling after
            // it's been removed from the queue entirely (deleted while in flight) - in that case there's nowhere
            // to swap it in, so just delete the now-orphaned cache file instead of leaving it on disk forever.
            while (_resampledSwapsReady.TryDequeue(out var resampled))
            {
                var index = providers.FindIndex(provider => string.Equals((provider as EnhancedAudioFileReader).OriginalFilePath, resampled.OriginalFilePath, StringComparison.OrdinalIgnoreCase));
                _resamplingInFlight.TryRemove(resampled.OriginalFilePath, out _);

                if (index == -1)
                {
                    resampled.Dispose();
                    if (!string.IsNullOrWhiteSpace(resampled.ServerFilepath))
                        FileManager.Instance.RemoveFile(resampled.ServerFilepath);
                    continue;
                }

                // If the track that just finished resampling happens to be whatever's sitting at the current
                // position (only possible via the synchronous-fallback path below racing a background resample
                // for the same track - rare, but not impossible), _currentProvider itself needs to be
                // redirected to the new object, not just the entry in `providers` - Read() always reads
                // _currentProvider directly, so leaving it pointed at the old object would mean the upgrade
                // never actually takes effect for playback even though _providers looks correct.
                var previous = providers[index] as EnhancedAudioFileReader;
                var wasCurrent = previous != null && previous.Equals(_currentProvider);
                providers[index] = resampled;
                if (wasCurrent) _currentProvider = resampled;
                previous?.Dispose();
            }

            var currentIndex = providers.FindIndex(provider => provider.Equals(_currentProvider));
            if (currentIndex == -1)
            {
                _providers = providers;
                ResetEnumerator();
                return;
            }

            // Defensive fallback: every normal transition path guarantees _currentProvider is already resampled
            // before it's ever set as current (the initial window in the constructor, or the lookahead upgrade
            // below completing before playback reaches a track). The one path that can't guarantee that is
            // RepeatAll wrapping back to the start of the queue - by the time a long queue's last track finishes,
            // everything at the front was likely evicted many tracks ago (RETENTION_WINDOW is only 1), so landing
            // back on track 0 can mean landing on a genuinely unsampled reader. Reading that directly would bypass
            // the resample/upmix pipeline entirely and reintroduce the "2x speed" class of bug (KNOWN_ISSUES.md
            // #22) for any mono or non-target-sample-rate source, so this can't be left to the normal background
            // path - resample synchronously, right here. Rare enough (at most once per full RepeatAll lap) that a
            // brief pause here is an acceptable trade-off for correctness over the seamless-in-the-common-case
            // background path used everywhere else.
            var current = providers[currentIndex] as EnhancedAudioFileReader;
            if (current != null && current.ServerFilepath == null)
            {
                _resamplingInFlight.TryRemove(current.OriginalFilePath, out _);
                var resampledProvidersFilePaths = FileManager.Instance.GetResampledProviderFileNames();
                var existingServerFileName = resampledProvidersFilePaths.FirstOrDefault(serverFilePath => HasMatchingServerFileName(serverFilePath, current.OriginalFilePath));
                var resampledCurrent = WriteResampledFile(current.OriginalFilePath, current.EqualizerPreset, existingServerFileName);

                providers[currentIndex] = resampledCurrent;
                _currentProvider = resampledCurrent;
                current.Dispose();
            }

            var windowStart = Math.Max(0, currentIndex - RETENTION_WINDOW);
            var windowEnd = Math.Min(providers.Count - 1, currentIndex + LOOKAHEAD_WINDOW);

            for (int i = windowStart; i <= windowEnd; i++)
            {
                var provider = providers[i] as EnhancedAudioFileReader;
                if (provider == null || provider.ServerFilepath != null) continue; // already resampled
                if (_resamplingInFlight.ContainsKey(provider.OriginalFilePath)) continue; // already in flight

                _resamplingInFlight.TryAdd(provider.OriginalFilePath, 0);
                var originalPath = provider.OriginalFilePath;
                var preset = provider.EqualizerPreset;

                Task.Run(() =>
                {
                    try
                    {
                        var resampledProvidersFilePaths = FileManager.Instance.GetResampledProviderFileNames();
                        var existingServerFileName = resampledProvidersFilePaths.FirstOrDefault(serverFilePath => HasMatchingServerFileName(serverFilePath, originalPath));
                        var resampled = WriteResampledFile(originalPath, preset, existingServerFileName);

                        // KNOWN_ISSUES.md - if Dispose() already ran (e.g. Player.Play() started a new playlist
                        // right as this lookahead resample kicked off), nothing will ever call
                        // EnsureWindowResampled again to drain this queue - previously this landed in
                        // _resampledSwapsReady forever, leaking both the open file handle and its cache .wav on
                        // disk for the rest of the process's life. Checking disposedValue here closes the common
                        // case (still in flight at Dispose time); Dispose(bool) itself also drains whatever's
                        // already sitting in the queue for the case where the resample finished just before it ran.
                        if (disposedValue)
                        {
                            resampled.Dispose();
                            if (!string.IsNullOrWhiteSpace(resampled.ServerFilepath))
                                FileManager.Instance.RemoveFile(resampled.ServerFilepath);
                        }
                        else
                        {
                            _resampledSwapsReady.Enqueue(resampled);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unable to resample {OriginalPath} for the lazy playback window", originalPath);
                    }
                    finally
                    {
                        _resamplingInFlight.TryRemove(originalPath, out _);
                    }
                });
            }

            // Anything resampled but now outside [windowStart, windowEnd] gets downgraded back to a cheap
            // unsampled reader - dispose the resampled reader (closes the cache file's handle), delete the cache
            // file, and replace it in-place with a fresh source-backed reader so Player.cs's polling still has a
            // valid TotalTime/OriginalFilePath to read for that queue row. Never touches the current track itself
            // (i between windowStart/windowEnd always includes currentIndex).
            for (int i = 0; i < providers.Count; i++)
            {
                if (i >= windowStart && i <= windowEnd) continue;

                var provider = providers[i] as EnhancedAudioFileReader;
                if (provider == null || provider.ServerFilepath == null) continue; // already unsampled, nothing to evict

                var cacheFilePath = provider.ServerFilepath;
                var replacement = new EnhancedAudioFileReader(serverFileName: null, originalFileName: provider.OriginalFilePath, equalizerPreset: provider.EqualizerPreset);
                providers[i] = replacement;
                provider.Dispose();
                FileManager.Instance.RemoveFile(cacheFilePath);
            }

            _providers = providers;
            ResetEnumerator();
        }

        public void ReOrderProviders(string[] paths) => _proposedOrder = paths.ToList();

        public void RemoveProvider(string[] paths)
        {
            var queuedProviders = _queuedProviders.ToList();
            var newQueuedProviders = new List<QueuedProviderInstruction>();

            foreach (var queuedProvider in queuedProviders)
            {
                var providers = queuedProvider.Providers.ToList();

                // Was `string.Equals(toBeRemoved.Select(...), (provider...))` - comparing an IEnumerable<string>
                // to a single string via the static object overload, which is always false. So a track that had
                // been queued to add but not yet merged into _providers could never actually be pulled back out
                // of that pending queue via this path (harmless on its own - AddQueuedProviders' removal branch
                // below still purges it from _providers once merged - but meant deleting a track before it ever
                // played would leave a dead/duplicate entry queued to be added anyway).
                providers.RemoveAll(provider => paths.Contains((provider as EnhancedAudioFileReader).OriginalFilePath));
                queuedProvider.Providers = providers;
                newQueuedProviders.Add(queuedProvider);
            }
            _queuedProviders = newQueuedProviders;
            _removedProviders.AddRange(paths);

            // A track can be deleted from the queue while its background resample is still in flight - it'll
            // still finish and land in _resampledSwapsReady, but EnsureWindowResampled's drain step already
            // handles that case (no matching entry left in _providers -> dispose + delete the orphaned cache
            // file instead of swapping it in), so nothing extra is needed here beyond letting _resamplingInFlight
            // naturally get cleared when that drain runs.
        }

        // Re-locates the enumerator against _currentProvider (the class field), not _enumerator.Current - the two
        // are normally the same object, but EnsureWindowResampled can replace whatever's sitting at the current
        // position with a freshly-resampled instance (and updates _currentProvider to match) before calling this,
        // in which case _enumerator's own stale internal reference would never be found in the new _providers list.
        private void ResetEnumerator()
        {
            _enumerator.Dispose();
            _enumerator = new PeekingEnumerator<ISampleProvider>(_providers);
            _enumerator.MoveToElement(_currentProvider);
        }

        // The "logical" current queue - _providers with any pending removal already subtracted and any
        // pending add already merged in, even though neither has actually been applied to _providers itself
        // yet (deferred until AddQueuedProviders runs at the next track change - see the note on that method).
        // Previously this only accounted for pending adds, not removals, which is what let a just-deleted
        // track keep counting toward Providers.Count()/HasNextProvider until the deferred purge caught up.
        private IEnumerable<ISampleProvider> GetProviders()
        {
            var providers = _removedProviders.Any()
                ? _providers.Where(provider => !_removedProviders.Contains((provider as EnhancedAudioFileReader).OriginalFilePath)).ToList()
                : _providers.ToList();

            if (!_queuedProviders?.Any() ?? true) return providers;

            foreach (var queuedProvider in _queuedProviders)
            {
                var index = ResolveAnchorIndex(providers, queuedProvider.IndexPath);

                if (queuedProvider.CanAppend)
                    providers.AddRange(index, queuedProvider.Providers);
                else
                    providers.InsertRange(index, queuedProvider.Providers);
            }

            return providers;
        }

        // A queued add is anchored to whatever track was "current" (IndexPath) at the moment it was queued -
        // but that anchor can be deleted before the add is ever merged in (here, or in AddQueuedProviders).
        // FindIndex then returns -1, and passing that straight to ListExtensions.AddRange/List<T>.InsertRange
        // throws ArgumentOutOfRangeException - that's the crash this fixes. Falling back to the end of the list
        // is a reasonable default: the add still lands in the playlist, just at the tail instead of wherever
        // its now-gone anchor used to be.
        private static int ResolveAnchorIndex(List<ISampleProvider> providers, string indexPath)
        {
            var index = providers.FindIndex(provider => string.Equals((provider as EnhancedAudioFileReader).OriginalFilePath, indexPath));
            return index == -1 ? providers.Count : index;
        }

        private bool HasEffectiveNextProvider()
        {
            var effectiveProviders = GetProviders().ToList();
            var currentIndex = effectiveProviders.FindIndex(provider => provider.Equals(_currentProvider));

            // currentIndex is -1 if _currentProvider itself was just removed (deleting the currently-playing
            // track) - that's a separate, not-yet-handled edge case; falling back to "no next" here is the safe
            // default rather than guessing.
            return currentIndex >= 0 && currentIndex < effectiveProviders.Count - 1;
        }

        private bool HasPreviuosProvider()
        {
            if (_enumerator.HasPrevious) return true;

            // _currentProvider is null once playback has reached the natural end of the playlist (see
            // HandleSequentialPlayback, which sets it to null when !_enumerator.HasNext) - the unguarded cast
            // below then threw a NullReferenceException on every UpdatePlaybackInformation() poll tick from
            // that point on. That call is wrapped in a try/catch that only logs, so the exception itself didn't
            // crash anything, but it aborted the rest of UpdatePlaybackInformation() every time - freezing
            // IsPlaying/HasNext/Tracks/etc. permanently once the playlist ended, the same "frozen playback info"
            // failure mode as the earlier delete-track bug. Nothing to rewind to with no current provider, so
            // false is the correct/safe result here, not a crash.
            // ('is not' pattern matching needs C# 9 - MusicPlayer targets C# 7.3, hence the `as` + null check
            // instead, same style as every other cast in this file.)
            var currentProvider = _currentProvider as EnhancedAudioFileReader;
            if (currentProvider == null) return false;

            var currentTime = currentProvider.CurrentTime.TotalSeconds;
            return currentTime > 10;
        }

        #region Dispose Pattern
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _enumerator?.Dispose();
                    _queuedProviders = null;

                    // Drain anything a background lookahead resample already finished and queued up but that
                    // never got the chance to be picked up by EnsureWindowResampled (e.g. Player.Play() disposing
                    // this provider to start a new playlist right as a resample landed). Without this, each
                    // entry's open file handle and its cache .wav on disk would leak for the rest of the process's
                    // life - nothing else will ever drain this queue once this instance is gone. Anything still
                    // genuinely in flight at this exact moment is instead caught by the disposedValue check inside
                    // the Task.Run closure below, once it completes.
                    while (_resampledSwapsReady.TryDequeue(out var resampled))
                    {
                        resampled.Dispose();
                        if (!string.IsNullOrWhiteSpace(resampled.ServerFilepath))
                            FileManager.Instance.RemoveFile(resampled.ServerFilepath);
                    }

                    // Cache files only ever exist for tracks that were actually resampled (ServerFilepath set) -
                    // with the lazy window that's normally just a handful, not the whole queue. Deleting them
                    // here (rather than leaving them for the next MusicServer restart's full-directory clear, the
                    // only other place this ever happened before) means switching to a new playlist via
                    // Player.Play() doesn't leave the previous one's cache behind for the rest of the session.
                    foreach (var provider in _providers)
                    {
                        var reader = provider as EnhancedAudioFileReader;
                        var cacheFilePath = reader?.ServerFilepath;
                        reader?.Dispose();
                        if (!string.IsNullOrWhiteSpace(cacheFilePath))
                            FileManager.Instance.RemoveFile(cacheFilePath);
                    }

                    _providers = null;
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion Dispose Pattern
    }
}
