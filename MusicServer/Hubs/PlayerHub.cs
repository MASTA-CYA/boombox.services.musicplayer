using Microsoft.AspNetCore.SignalR;
using MusicPlayer.Common;
using MusicPlayer.Player;
using MusicPlayer.Player.Models;
using MusicServer.Helpers;
using Newtonsoft.Json;
using System.Runtime.Versioning;

namespace MusicServer.Hubs
{
    public class PlayerHub : Hub
    {

        #region Public Methods
        public async Task InitializePlayerAsync()
        {
            ExecuteOnPlayerThread(InitializePlayer);
            await Task.CompletedTask;
        }

        public async Task GetPlaybackInformationUpdateAsync() => await SendPlaybackInformationAsync();

        public async Task PlayAsync(string[]? paths)
        {
            if (paths == null)
                ExecuteOnPlayerThread(ResumeAudio);
            else
                ExecuteOnPlayerThread(() => PlayAudioFiles(paths));

            _ = Task.Run(ServerHttpClient.Instance.StartPlaybackInformationBroadcastAsync);
            await Task.CompletedTask;
        }

        public async Task PauseAsync()
        {
            ExecuteOnPlayerThread(PauseAudio);
            await Task.CompletedTask;
        }

        public async Task PlayNextAsync()
        {
            ExecuteOnPlayerThread(PlayNext);
            await Task.CompletedTask;
        }

        public async Task PlayPreviousAsync()
        {
            ExecuteOnPlayerThread(PlayPrevious);
            await Task.CompletedTask;
        }

        public async Task TogglePlayerModeAsync()
        {
            ExecuteOnPlayerThread(TogglePlayerMode);
            await Task.CompletedTask;
        }

        public async Task ReorderNowPlayingAsync(string[] paths)
        {
            ExecuteOnPlayerThread(() => ReorderNowPlaying(paths));
            await Task.CompletedTask;
        }

        public async Task RemoveNowPlayingTrackAsync(string[] paths)
        {
            ExecuteOnPlayerThread(() => RemoveNowPlayingTrack(paths));
            await Task.CompletedTask;
        }

        public async Task AddToNowPlayingAsync(string[] paths, bool canAppend, string? indexPath)
        {
            ExecuteOnPlayerThread(() => AddToNowPlaying(paths, canAppend, indexPath));
            await Task.CompletedTask;
        }

        public async Task<string> GetEqualizerPresetsAsync()
        {
            await Task.CompletedTask;
            return JsonConvert.SerializeObject(Player.Instance.GetMergedEqualizerPresets(), JsonSerializationHelper.NamingSerializerSettings);
        }

        public async Task SetEqualizerPresetsAsync(EqualizerPreset preset)
        {
            ExecuteOnPlayerThread(() => ApplyEqualizerPreset(preset));
            await Task.CompletedTask;
        }

        #endregion Public Methods

        #region Private Methods
        private async Task SendPlaybackInformationAsync()
        {
            var playbackInfo = Player.Instance.PlaybackInformation;

            if (playbackInfo == null) return;

            var playbackInfoJson = System.Text.Json.JsonSerializer.Serialize(playbackInfo, JsonSerializationHelper.SerializerOptions);

            await Clients.All.SendAsync("ReceivePlaybackInformation", playbackInfoJson);
        }

        [SupportedOSPlatform("windows")]
        private void ExecuteOnPlayerThread(Action action)
        {
            var thread = new Thread(() => ErrorHandlingAction(action));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }

        private void ErrorHandlingAction(Action action)
        {
            try { action.Invoke(); }
            catch (Exception ex) { Task.Run(async () => await Clients.All.SendAsync("ReceivePlayerHubError", ex.Message)); }
        }
        private async Task ErrorHandlingActionAsync(Func<Task> action)
        {
            try { await action.Invoke(); }
            catch (Exception ex) { await Clients.All.SendAsync("ReceivePlayerHubError", ex.Message); }
        }

        [SupportedOSPlatform("windows")]
        private async Task ExecuteOnPlayerThreadAsync(Func<Task> action)
        {
            var thread = new Thread(async () => await ErrorHandlingActionAsync(action));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            await Task.CompletedTask;
        }

        private static void InitializePlayer() => Player.Instance.InitializePlayer();
        private static void PlayAudioFiles(string[] paths) => Player.Instance.Play(paths);
        private static void PauseAudio() => Player.Instance.Pause();
        private static void ResumeAudio() => Player.Instance.Resume();
        private static void PlayNext() => Player.Instance.PlayNext();
        private static void PlayPrevious() => Player.Instance.PlayPrevious();
        private static void TogglePlayerMode() => Player.Instance.TogglePlaybackMode();
        private static void AddToNowPlaying(string[] paths, bool canAppend, string? indexPath) => Player.Instance.AddToNowPlaying(paths, canAppend, indexPath);
        private static void ReorderNowPlaying(string[] paths) => Player.Instance.ReorderNowPlayingPlaylist(paths);
        private static void RemoveNowPlayingTrack(string[] paths) => Player.Instance.RemoveNowPlayingTrack(paths);
        private static void ApplyEqualizerPreset(EqualizerPreset preset) => Player.Instance.ApplyEqualizerPreset(preset);

        #endregion Private Methods
    }
}
