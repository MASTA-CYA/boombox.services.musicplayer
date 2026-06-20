using System.Collections.Generic;
using System.Linq;

namespace MusicPlayer.Extensions
{
    public static class DictionaryExtensions
    {
        public static void AddEntries<TKey, TValue>(this Dictionary<int, TValue> dictionary, int index, IEnumerable<TValue> collection)
        {
            var trackingIndex = index;
            var lastIndex = dictionary.Count - 1;
            var shiftedEntries = new List<KeyValuePair<int, TValue>>();

            foreach (var newValue in collection)
            {
                if (trackingIndex >= lastIndex)
                {
                    dictionary.Add(trackingIndex, newValue);
                }
                else
                {
                    var currentEntry = dictionary.ElementAt(trackingIndex);
                    shiftedEntries.Add(currentEntry);
                    dictionary[currentEntry.Key] = newValue;
                }

                trackingIndex++;
            }

            //shiftedEntries.v
            //dictionary.AddEntries(trackingIndex, shiftedEntries)
        }
    }
}
