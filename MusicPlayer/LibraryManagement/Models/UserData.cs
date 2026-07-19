using MusicPlayer.FileManagement.Interfaces;
using MusicPlayer.Helpers;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace MusicPlayer.LibraryManagement.Models
{
    public class UserData : IFileWritable
    {
        public List<string> FavouriteTrackPaths { get; set; }

        public (string id, string content) GetFileContent()
            => (id: "UserData", content: JsonConvert.SerializeObject(this, JsonSerializationHelper.FileSerializerSettings));
    }
}
