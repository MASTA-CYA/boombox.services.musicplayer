using MusicPlayer.AutoScrollManagement.Models;
using System;
using System.Threading.Tasks;

namespace MusicPlayer.AutoScrollManagement
{
    public sealed class ScrollManager
    {
        private readonly ScrollPosition _scrollPosition;

        #region Singleton
        private static readonly Lazy<ScrollManager> _instance = new Lazy<ScrollManager>(() => new ScrollManager());
        public static ScrollManager Instance { get => _instance.Value; }

        private ScrollManager()
        {
            _scrollPosition = new ScrollPosition();
        }

        #endregion Singleton

        public async Task UpdateScrollPositionAsync(int horizontal, int vertical)
        {
            _scrollPosition.Horizontal = horizontal;
            _scrollPosition.Vertical = vertical;
            await Task.CompletedTask;
        }

        public async Task<ScrollPosition> GetScrollPositionAsync()
        {
            await Task.CompletedTask;
            return _scrollPosition;
        }
    }
}
