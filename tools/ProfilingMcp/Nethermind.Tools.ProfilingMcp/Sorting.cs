// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.ProfilingMcp;

internal static class Sorting
{
    /// <summary>Sorts <paramref name="items"/> by descending key, keeping items with equal keys in their original order.</summary>
    /// <remarks><see cref="List{T}.Sort(Comparison{T})"/> is unstable, so ties are broken by the original index.</remarks>
    public static void SortDescending<T, TKey>(List<T> items, Func<T, TKey> key)
    {
        TKey[] keys = new TKey[items.Count];
        int[] order = new int[items.Count];
        for (int i = 0; i < order.Length; i++)
        {
            keys[i] = key(items[i]);
            order[i] = i;
        }

        Comparer<TKey> comparer = Comparer<TKey>.Default;
        Array.Sort(order, (a, b) =>
        {
            int byKey = comparer.Compare(keys[b], keys[a]);
            return byKey != 0 ? byKey : a.CompareTo(b);
        });

        T[] unsorted = [.. items];
        for (int i = 0; i < order.Length; i++)
            items[i] = unsorted[order[i]];
    }
}
