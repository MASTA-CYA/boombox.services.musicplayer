using MusicPlayer.PlaylistManagement.Models;
using System.Collections.Generic;

namespace MusicPlayer.Player.Models
{
    public class PlaybackInformation
    {
        public PlayerState PlayerState { get; set; }
        public List<PlaylistTrack> Tracks { get; set; }
        public bool HasReachedEndOfPlaylist { get; set; }

        public PlaybackInformation()
        {
            PlayerState = new PlayerState();
            Tracks = new List<PlaylistTrack>();
        }
    }
}