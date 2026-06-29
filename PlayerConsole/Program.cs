using MusicPlayer.FileManagment;
using MusicPlayer.Helpers;
using MusicPlayer.PlaylistManagement.Models;
using Newtonsoft.Json;

namespace PlayerConsole
{
    internal class Program
    {
        [STAThread]
        static async Task Main(string[] args)
        {
            var jsonString = FileManager.Instance.Read("C:\\Users\\CYA\\AppData\\Local\\MongoDBCompass\\app-1.49.9\\music_server.playlists.json");
            var root = JsonConvert.DeserializeObject<List<Playlist>>(jsonString, settings: JsonSerializationHelper.FileSerializerSettings);
            var tracks = new List<PlaylistTrack>();

            foreach (var track in root[1].Tracks)
            {
                track.Image = null;
                tracks.Add(track);
            }
            root[1].Tracks = tracks;
            var fixedJson = JsonConvert.SerializeObject(root, JsonSerializationHelper.FileSerializerSettings);
            FileManager.Instance.Write("C:\\Users\\CYA\\AppData\\Local\\MongoDBCompass\\app-1.49.9\\test.json", fixedJson);

            //var mediaInfo = new MediaInfoWrapper(@"C:\Users\CYA\Music\Gorillaz - Humanz (Japanese Edition)\01 - Interlude New World.flac");
            //Console.WriteLine(mediaInfo.Tags.DiscNumber);
            //var tagLibInfo = MediaFile.Read(@"C:\Users\CYA\Music\Gorillaz - Humanz (Japanese Edition)\01 - Interlude New World.flac");
            //Console.WriteLine(tagLibInfo.Tag.DiscNumber);

            Console.ReadKey();
        }

        private static void ExecuteOnPlayerThread(Action action)
        {
            var thread = new Thread(action.Invoke);
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
    }
}
