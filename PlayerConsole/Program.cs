using MusicPlayer.Common;
using MusicPlayer.FileManagment;
using MusicPlayer.Player.Models;
using Newtonsoft.Json.Linq;

namespace PlayerConsole
{
    internal class Program
    {
        [STAThread]
        static async Task Main(string[] args)
        {
            var configPath = Path.Combine(Constants.LIBRARY_DIRECTORY, "config.json");
            var jsonString = FileManager.Instance.Read(configPath);
            var root = JObject.Parse(jsonString);
            var presetsToken = root["equalizerPresets"];
            var equalizerConfig = presetsToken.ToObject<List<EqualizerPreset>>();

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
