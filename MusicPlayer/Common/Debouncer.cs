using MusicPlayer.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayer.Common
{
    public sealed class Debouncer
    {
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
                Task.Run(async () => await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "Debouncer | Debounce",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                }));
                Console.WriteLine(ex.Message);
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
                await ServerHttpClient.Instance.LogEntryAsync(new LogEntry
                {
                    Severity = Severity.Error,
                    Source = "Debouncer | DebounceAsync",
                    Line = ex.Message,
                    TimeStamp = DateTime.Now,
                    Exception = ex
                });
                Console.WriteLine(ex.Message);
            }
        }
    }
}
