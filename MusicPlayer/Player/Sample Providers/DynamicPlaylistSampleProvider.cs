using MusicPlayer.Common;
using MusicPlayer.Extensions;
using MusicPlayer.FileManagment;
using MusicPlayer.Models;
using MusicPlayer.Player.Models;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicPlayer.Player
{
    public class DynamicPlaylistSampleProvider : ISampleProvider, IDisposable
    {
        private const int SAMPLE_RATE = 44100;
        private const float BAND_WIDTH_Q = 1.414f;

        private PeekingEnumerator<ISampleProvider> _enumerator;
        private IEnumerable<QueuedProviderInstruction> _queuedProviders;
        private List<string> _proposedOrder;
        private List<string> _removedProviders;
        private bool disposedValue;

        private IEnumerable<ISampleProvider> _providers;
        public IEnumerable<ISampleProvider> Providers { get => GetProviders(); }
        private ISampleProvider _currentProvider;

        private readonly float[] _flatEqualizerFrequencyGains;
        private readonly BiQuadFilter[,] _equalizerFrequencyFilters;
        private readonly float[] _activeFrequencyBandGains;

        public ISampleProvider CurrentProvider { get => _currentProvider; }
        public bool HasPreviousProvider { get => HasPreviuosProvider(); }
        public bool HasNextProvider { get => _enumerator.HasNext || _queuedProviders.Any(); }
        public WaveFormat WaveFormat { get; }

        public event EventHandler<CurrentProviderEventArgs> ReachedEndOfProvider;
        public event EventHandler<ProviderPathsEventArgs> ReachedEndOfPlaylist;
        public event EventHandler QueuedProvidersAdded;

        public DynamicPlaylistSampleProvider(Dictionary<string, EqualizerPreset> trackConfigurations, float[] frequencies)
        {
            _providers = GetResampledProviders(trackConfigurations);
            _enumerator = new PeekingEnumerator<ISampleProvider>(_providers);
            _currentProvider = _enumerator.Current;

            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SAMPLE_RATE, 2);
            _queuedProviders = new List<QueuedProviderInstruction>();
            _proposedOrder = new List<string>();
            _removedProviders = new List<string>();

            _flatEqualizerFrequencyGains = frequencies;
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
                    _equalizerFrequencyFilters[band, ch] = BiQuadFilter.PeakingEQ(sampleRate, _flatEqualizerFrequencyGains[band], BAND_WIDTH_Q, _activeFrequencyBandGains[band]);
        }

        public void SetFrequencyBandGain(int bandIndex, float gainDb)
        {
            if (bandIndex < 0 || bandIndex >= 9) return;

            _activeFrequencyBandGains[bandIndex] = gainDb;

            for (int ch = 0; ch < WaveFormat.Channels; ch++)
                _equalizerFrequencyFilters[bandIndex, ch].SetPeakingEq(WaveFormat.SampleRate, _flatEqualizerFrequencyGains[bandIndex], BAND_WIDTH_Q, _activeFrequencyBandGains[bandIndex]);
        }

        public void SetPresetFrequencyBandGains(EqualizerFrequencyBand[] bands = null)
        {
            try
            {
                var orderedBandGains = _flatEqualizerFrequencyGains;

                if (bands != null)
                    orderedBandGains = bands.OrderBy(band => band.Frequency).Select(orderedBand => orderedBand.Gain).ToArray();

                if (orderedBandGains.Any(band => band > 15 || band < -15))
                    throw new InvalidDataException("Band gain exceeded limits");

                for (int i = 0; i < orderedBandGains.Length; i++)
                    SetFrequencyBandGain(i, orderedBandGains[i]);
            }
            catch (Exception ex)
            {
                Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "SetPresetFrequencyBandGains",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                }));
                Console.WriteLine(ex);
            }


        }

        private IEnumerable<ISampleProvider> GetUnsampledProviders(Dictionary<string, EqualizerPreset> trackConfigurations)
            => trackConfigurations.Keys.Select(file => new EnhancedAudioFileReader(serverFileName: null, originalFileName: file, equalizerPreset: trackConfigurations[file]));

        private IEnumerable<ISampleProvider> GetResampledProviders(Dictionary<string, EqualizerPreset> trackConfigurations)
        {
            var resampledProviders = new List<ISampleProvider>();
            var resampledProvidersFilePaths = FileManager.Instance.GetResampledProviderFileNames();
            Parallel.ForEach(trackConfigurations.ToList(), config => WriteResampledFile(config, ref resampledProviders, resampledProvidersFilePaths.FirstOrDefault(serverFilePath => HasMatchingServerFileName(serverFilePath, config.Key))));
            return resampledProviders.OrderBy(provider => trackConfigurations.Keys.ToList().IndexOf((provider as EnhancedAudioFileReader).OriginalFilePath));
        }

        private void WriteResampledFile(KeyValuePair<string, EqualizerPreset> config, ref List<ISampleProvider> reSampledProviders, string serverFileName = null)
        {
            if (!string.IsNullOrWhiteSpace(serverFileName))
            {
                reSampledProviders.Add(new EnhancedAudioFileReader(serverFileName: serverFileName, originalFileName: config.Key, equalizerPreset: config.Value));
                return;
            }

            using (var reader = new AudioFileReader(config.Key))
            {
                var resampler = new WdlResamplingSampleProvider(reader, SAMPLE_RATE);
                var resampledFilename = Path.Combine(new string[] { Constants.RESAMPLED_PROVIDERS_DIRECTORY, $"{Path.GetFileNameWithoutExtension(config.Key)}.wav" });
                WaveFileWriter.CreateWaveFile16(resampledFilename, resampler);
                reSampledProviders.Add(new EnhancedAudioFileReader(serverFileName: resampledFilename, originalFileName: config.Key, equalizerPreset: config.Value));
            }
        }

        private bool HasMatchingServerFileName(string serverPath, string originalPath)
        {
            var serverFileName = Path.GetFileNameWithoutExtension(serverPath);
            var originalFileName = Path.GetFileNameWithoutExtension(originalPath);

            return string.Equals(serverFileName, originalFileName);
        }

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
        }

        public void AddProviders(Dictionary<string, EqualizerPreset> configs, bool canAppend = true, string indexPath = null)
        {
            try
            {
                var queue = _queuedProviders.ToList();

                if (!queue.Any())
                {
                    var trackConfigurations = new Dictionary<string, EqualizerPreset>();

                    queue.Add(new QueuedProviderInstruction
                    {
                        Providers = GetResampledProviders(configs),
                        CanAppend = canAppend,
                        IndexPath = indexPath,
                    });
                    _queuedProviders = queue;
                    return;
                }

                if (string.IsNullOrWhiteSpace(indexPath))
                {
                    var lastAppendingQueuedProvider = queue.Find(provider => provider.CanAppend
                        && string.Equals(provider.IndexPath, (_providers.Last() as EnhancedAudioFileReader).OriginalFilePath));
                    var currentProviders = lastAppendingQueuedProvider.Providers.ToList();
                    currentProviders.AddRange(GetResampledProviders(configs));
                    lastAppendingQueuedProvider.Providers = currentProviders;

                    return;
                }

                foreach (var queuedProvider in queue.ToList())
                {
                    var providers = queuedProvider.Providers.ToList();
                    var index = providers.FindIndex(provider => string.Equals((provider as EnhancedAudioFileReader).OriginalFilePath, indexPath));

                    if (index != -1)
                    {
                        if (canAppend)
                            providers.AddRange(index, GetResampledProviders(configs));
                        else
                            providers.InsertRange(index, GetResampledProviders(configs));

                        queuedProvider.Providers = providers;
                        var amendedInstruction = queue.FirstOrDefault(instruction => string.Equals(instruction.IndexPath, queuedProvider.IndexPath));
                        amendedInstruction = queuedProvider;
                        _queuedProviders = queue;
                        return;
                    }
                }

                queue.Add(new QueuedProviderInstruction
                {
                    Providers = GetResampledProviders(configs),
                    CanAppend = canAppend,
                    IndexPath = indexPath,
                });
                _queuedProviders = queue;
            }
            catch (Exception ex)
            {
                Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "AddProviders",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                }));
                Console.WriteLine(ex);
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
            ReachedEndOfProvider?.Invoke(this, new CurrentProviderEventArgs(_currentProvider));
            AddQueuedProviders();

            switch (Player.Instance.PlaybackInformation.PlayerState.Mode)
            {
                case PlaybackMode.Shuffle:
                    HandleShufflePlayback();
                    SetPresetFrequencyBandGains((_currentProvider as EnhancedAudioFileReader).EqualizerPreset?.FrequencyBands?.ToArray());
                    break;
                case PlaybackMode.RepeatAll:
                    HandleRepeatAllPlayback();
                    SetPresetFrequencyBandGains((_currentProvider as EnhancedAudioFileReader).EqualizerPreset?.FrequencyBands?.ToArray());
                    break;
                case PlaybackMode.RepeatOne:
                    HandleRepeatOnePlayback();
                    SetPresetFrequencyBandGains((_currentProvider as EnhancedAudioFileReader).EqualizerPreset?.FrequencyBands?.ToArray());
                    break;
                case PlaybackMode.Sequential:
                default:
                    HandleSequentialPlayback();
                    SetPresetFrequencyBandGains((_currentProvider as EnhancedAudioFileReader).EqualizerPreset?.FrequencyBands?.ToArray());
                    break;
            }
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

            var nextAvailableSources = _providers.Where(provider => !provider.Equals(_currentProvider)).ToList();
            nextAvailableSources.Shuffle();

            var shuffledAvailableSources = nextAvailableSources.Prepend(_currentProvider);
            _providers = shuffledAvailableSources;

            _enumerator.Dispose();
            _enumerator = new PeekingEnumerator<ISampleProvider>(_providers);

            HandleSequentialPlayback();
        }

        private void AddQueuedProviders()
        {
            if (!_queuedProviders.Any())
            {
                if (_proposedOrder.Any())
                {
                    var orderedProviders = _providers.OrderBy(provider => _proposedOrder.IndexOf((provider as EnhancedAudioFileReader).OriginalFilePath)).AsEnumerable();
                    _providers = orderedProviders.AsEnumerable();
                    ResetEnumerator();
                    _proposedOrder.Clear();
                }
                return;
            }

            var providers = _providers.ToList();
            providers.RemoveAll(provider => _removedProviders.Contains((provider as EnhancedAudioFileReader).OriginalFilePath));
            _removedProviders.Clear();

            foreach (var queuedProvider in _queuedProviders)
            {
                var index = providers.FindIndex(provider => string.Equals((provider as EnhancedAudioFileReader).OriginalFilePath, queuedProvider.IndexPath));

                if (queuedProvider.CanAppend)
                    providers.AddRange(index, queuedProvider.Providers);
                else
                    providers.InsertRange(index, queuedProvider.Providers);
            }

            if (_proposedOrder.Any())
            {
                _providers = providers.OrderBy(provider => _proposedOrder.IndexOf((provider as EnhancedAudioFileReader).OriginalFilePath)).AsEnumerable();
                _proposedOrder.Clear();
            }
            else
            {
                _providers = providers.AsEnumerable();
            }

            var queue = _queuedProviders.ToList();
            queue.Clear();
            _queuedProviders = queue;
            ResetEnumerator();

            QueuedProvidersAdded.Invoke(this, EventArgs.Empty);
        }

        public void ReOrderProviders(string[] paths) => _proposedOrder = paths.ToList();

        public void RemoveProvider(string[] paths)
        {
            var queuedProviders = _queuedProviders.ToList();
            var newQueuedProviders = new List<QueuedProviderInstruction>();

            foreach (var queuedProvider in queuedProviders)
            {
                var providers = queuedProvider.Providers.ToList();
                var toBeRemoved = providers.FindAll(provider => paths.Contains((provider as EnhancedAudioFileReader).OriginalFilePath));

                if (toBeRemoved == null)
                {
                    newQueuedProviders.Add(queuedProvider);
                    continue;
                }

                providers.RemoveAll(provider => string.Equals(toBeRemoved.Select(remove => (remove as EnhancedAudioFileReader).OriginalFilePath), (provider as EnhancedAudioFileReader).OriginalFilePath));
                queuedProvider.Providers = providers;
                newQueuedProviders.Add(queuedProvider);
            }
            _queuedProviders = newQueuedProviders;
            _removedProviders.AddRange(paths);
        }

        private void ResetEnumerator()
        {
            var currentProvider = _enumerator.Current;
            _enumerator.Dispose();
            _enumerator = new PeekingEnumerator<ISampleProvider>(_providers);
            _enumerator.MoveToElement(currentProvider);
        }

        private IEnumerable<ISampleProvider> GetProviders()
        {
            if (!_queuedProviders?.Any() ?? true) return _providers;

            var providers = _providers.ToList();
            foreach (var queuedProvider in _queuedProviders)
            {
                var index = providers.FindIndex(provider => string.Equals((provider as EnhancedAudioFileReader).OriginalFilePath, queuedProvider.IndexPath));

                if (queuedProvider.CanAppend)
                    providers.AddRange(index, queuedProvider.Providers);
                else
                    providers.InsertRange(index, queuedProvider.Providers);
            }

            return providers;
        }

        private bool HasPreviuosProvider()
        {
            if (_enumerator.HasPrevious) return true;

            var currentTime = (_currentProvider as EnhancedAudioFileReader).CurrentTime.TotalSeconds;
            if (currentTime > 10) return true;

            return false;
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

                    foreach (var provider in _providers)
                        (provider as EnhancedAudioFileReader).Dispose();

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
