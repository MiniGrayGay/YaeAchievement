using System.ComponentModel;

namespace System.Collections.Generic {

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class CollectionExtensions {

        public static IDictionary<TKey, TValue> RemoveValues<TKey, TValue>(
            this IDictionary<TKey, TValue> dictionary, params TKey[] keys
        ) {
            foreach (var key in keys) {
                dictionary.Remove(key);
            }
            return dictionary;
        }
    }
}

namespace System.Linq {

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class EnumerableExtensions {

        public static IEnumerable<IGrouping<TKey, TKey>> GroupKeys<TKey, TValue>(
            this IEnumerable<Dictionary<TKey, TValue>> source,
            Func<TValue, bool> condition
        ) where TKey : notnull => source
            .SelectMany(dict => dict.Where(pair => condition(pair.Value)).Select(pair => pair.Key))
            .GroupBy(x => x);
    }
}
