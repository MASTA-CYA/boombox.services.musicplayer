using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace MusicPlayer.Extensions
{
    public static class ListExtensions
    {
        public static void AddRange<T>(this IList<T> list, int index, IEnumerable<T> collection)
        {
            if (list == null) throw new ArgumentNullException(nameof(list));
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (index < 0 || index > list.Count) throw new ArgumentOutOfRangeException(nameof(index));


            if (!(list is List<T> concreteList))
                throw new InvalidOperationException("List is not of type List<T>");

            if (concreteList.Count == index)
            {
                concreteList.AddRange(collection);
                return;
            }

            concreteList.InsertRange(index + 1, collection);
        }

        public static void Shuffle<T>(this IList<T> list)
        {
            Random rng = new Random();
            int length = list.Count;

            while (length > 1)
            {
                length--;
                int randomIndex = rng.Next(length + 1);
                T value = list[randomIndex];
                list[randomIndex] = list[length];
                list[length] = value;
            }
        }

        public static void ToConcurrentBag<T>(this IList<T> list) => new ConcurrentBag<T>(list);
    }
}
