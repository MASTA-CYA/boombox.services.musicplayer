using System;

namespace MusicPlayer.LibraryManagement.Models
{
    public class FavouriteTrackEventArgs : EventArgs
    {
        public string FilePath { get; private set; }
        public bool IsFavourite { get; private set; }

        public FavouriteTrackEventArgs(string path, bool isFavourite)
        {
            FilePath = path;
            IsFavourite = isFavourite;
        }
    }
}
