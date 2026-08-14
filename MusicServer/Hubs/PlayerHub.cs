using Microsoft.AspNetCore.SignalR;
using MusicPlayer.Common;
using MusicPlayer.LibraryManagement;
using MusicPlayer.Player;
using MusicPlayer.Player.Models;
using MusicServer.Helpers;
using MusicServer.Startup;
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

            PlaybackBroadcast.Start();
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

        // Broadcasts immediately afterward for the same reason SetAudioOutputAsync does - PlaybackBroadcast's
        // 500ms loop only runs while something is actually playing, so without this, reordering while
        // paused/stopped would update the backend but never reach the UI until playback resumed.
        public async Task ReorderNowPlayingAsync(string[] paths)
        {
            ExecuteOnPlayerThread(() => ReorderNowPlaying(paths));
            await SendPlaybackInformationAsync();
        }

        // Broadcasts immediately afterward for the same reason ReorderNowPlayingAsync/SetAudioOutputAsync do -
        // PlaybackBroadcast's 500ms loop only runs while something is actually playing, so without this,
        // deleting a track while paused/stopped would update the backend but never reach the UI until playback
        // resumed.
        public async Task RemoveNowPlayingTrackAsync(string[] paths)
        {
            ExecuteOnPlayerThread(() => RemoveNowPlayingTrack(paths));
            await SendPlaybackInformationAsync();
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

        // Everything below backs Settings' "Equalizer" tab. None of it touches the live audio pipeline
        // (_playlistProvider/_audioPlayer) the way Play/Pause/SetAudioOutput/SetEqualizerPresets above do, so
        // unlike those, there's no need to marshal onto the STA player thread via ExecuteOnPlayerThread - it's
        // plain Mongo/JSON I/O and can run directly on whatever thread SignalR invokes this on.
        public async Task<string> GetEqualizerManagementDataAsync()
        {
            var presets = await MongoDbClient.Instance.GetEqualizerPresetsAsync();
            var assignments = await LibraryManager.Instance.GetTrackEqualizerAssignmentsAsync();
            return JsonConvert.SerializeObject(new { presets, assignments }, JsonSerializationHelper.NamingSerializerSettings);
        }

        public async Task<string> CreateEqualizerPresetAsync(string name)
        {
            var preset = await Player.Instance.CreateEqualizerPresetAsync(name);
            return JsonConvert.SerializeObject(preset, JsonSerializationHelper.NamingSerializerSettings);
        }

        public async Task UpdateNamedEqualizerPresetAsync(EqualizerPreset preset) => await Player.Instance.UpdateNamedEqualizerPresetAsync(preset);

        public async Task DeleteEqualizerPresetAsync(Guid guid) => await Player.Instance.DeleteEqualizerPresetAsync(guid);

        // GetEqualizerManagementDataAsync's assignment list is deliberately lightweight (album/track name, path,
        // and just the assigned preset's Guid - see TrackEqualizerAssignment) rather than embedding every
        // assignment's full band data up front. This fetches one specific per-track preset's full bands, on
        // demand, only when the user actually opens it for editing.
        public async Task<string> GetTrackEqualizerPresetAsync(string trackPath)
        {
            var preset = await MongoDbClient.Instance.GetEqualizerPresetAsync(trackPath);
            return JsonConvert.SerializeObject(preset, JsonSerializationHelper.NamingSerializerSettings);
        }

        public async Task UpdateTrackEqualizerPresetAsync(string trackPath, EqualizerPreset preset) => await LibraryManager.Instance.UpdateTrackEqualizerPresetAsync(trackPath, preset);

        public async Task DeleteTrackEqualizerPresetAsync(string trackPath) => await LibraryManager.Instance.DeleteTrackEqualizerPresetAsync(trackPath);

        // Runs on the same STA player thread as every other action here - both AsioOut and WasapiOut are
        // COM-based. Broadcasts the updated PlaybackInformation immediately afterward rather than waiting for
        // PlaybackBroadcast's 500ms loop, since that loop only runs while something is actually playing - without
        // this, switching output while paused/stopped would silently update the backend but never reach the UI.
        public async Task SetAudioOutputAsync(AudioOutput output)
        {
            ExecuteOnPlayerThread(() => SetAudioOutput(output));
            await SendPlaybackInformationAsync();
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
        private static void SetAudioOutput(AudioOutput output) => Player.Instance.SetAudioOutput(output);

        #endregion Private Methods
    }
}
