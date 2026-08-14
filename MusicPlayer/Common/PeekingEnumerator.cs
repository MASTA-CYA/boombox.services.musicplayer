using System.Collections;
using System.Collections.Generic;

namespace MusicPlayer.Player
{
    public class PeekingEnumerator<T> : IEnumerator<T>
    {
        private IEnumerable<T> _source;
        private IEnumerator<T> _enumerator;
        private T _previous;
        private T _current;
        private T _next;

        public bool HasNext => _next != null;
        public bool HasPrevious => _previous != null;
        public T Current => _current;
        object IEnumerator.Current => _current;
        public IEnumerable<T> Source { get => _source; }

        public PeekingEnumerator(IEnumerable<T> enumerable)
        {
            _source = enumerable;
            _enumerator = _source.GetEnumerator();
            MoveNext();
        }

        public void ResetToPreviousElement()
        {
            var previousElement = _previous;
            Reset();

            // Bounded by HasNext (and null-guarded) rather than looping purely on inequality - if
            // previousElement is no longer in _source (e.g. it was removed from the playlist), _current would
            // never equal it and MoveNext() keeps returning false once exhausted without changing _current,
            // spinning forever. Best effort now: walk as far as possible, then stop at whatever's left.
            while (_current != null && !_current.Equals(previousElement) && HasNext)
                MoveNext();
        }

        public void MoveToElement(T element)
        {
            // See ResetToPreviousElement - same unbounded-loop risk if element was removed from _source (e.g.
            // DynamicPlaylistSampleProvider.ResetEnumerator trying to re-locate a just-finished track that was
            // also just deleted from the playlist) between when it was captured and when this runs.
            while (_current != null && !_current.Equals(element) && HasNext)
                MoveNext();
        }

        #region Interface Members

        public bool MoveNext()
        {
            if (_current == null)
            {
                _enumerator.MoveNext();
                _current = _enumerator.Current;
            }
            else if (_next != null)
            {
                _previous = _current;
                _current = _next;
            }
            else return false;

            var hasMovedToNext = _enumerator.MoveNext();
            _next = hasMovedToNext ? _enumerator.Current : default;

            return hasMovedToNext;
        }

        public void Reset()
        {
            _enumerator = _source.GetEnumerator();
            _previous = default;
            _current = default;
            _next = default;

            MoveNext();
        }

        public void Dispose()
        {
            _previous = default;
            _current = default;
            _next = default;
            _source = default;
            _enumerator.Dispose();
        }

        #endregion Interface Members
    }
}
