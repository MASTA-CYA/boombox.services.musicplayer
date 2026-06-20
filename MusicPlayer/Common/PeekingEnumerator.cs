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

            while (!_current.Equals(previousElement))
                MoveNext();
        }

        public void MoveToElement(T element)
        {
            while (!_current.Equals(element))
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
