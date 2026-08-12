using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayer.Common
{
    public sealed class Debouncer
    {
        private static readonly ILogger _logger = AppLogger.CreateLogger<Debouncer>();

        private CancellationTokenSource _cancellationTokenSource;

        public void Debounce(Action action, int milliseconds = 500)
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                Task.Run(async () => await Task.Delay(milliseconds, _cancellationTokenSource.Token)).GetAwaiter().GetResult();
                action.Invoke();
            }
            catch (TaskCanceledException ex)
            {
                // Expected, routine outcome — Debounce cancels its own pending delay every time it's called again
                // before the previous one finished, which is the whole point of a debouncer. Logged at Debug (below
                // the app's Information floor) rather than Warning, so a burst of file-watcher events doesn't spam
                // the log file or, worse, the live SignalRErrorSink broadcast to connected clients.
                _logger.LogDebug(ex, "Debounce cancelled");
            }
        }

        public async Task DebounceAsync(Func<CancellationToken, Task> action, int milliseconds = 500)
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                await Task.Delay(milliseconds, _cancellationTokenSource.Token);
                await action.Invoke(_cancellationTokenSource.Token);
            }
            catch (TaskCanceledException ex)
            {
                // Same reasoning as Debounce above — cancellation here is expected, not an error.
                _logger.LogDebug(ex, "DebounceAsync cancelled");
            }
        }
    }
}
