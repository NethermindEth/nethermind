// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Collections;

/// <summary>
/// A pooled, index-addressable list backed by fixed-size 256-element segments instead of one contiguous array.
/// </summary>
/// <remarks>
/// A single doubling array would, once past the <see cref="ArrayPool{T}"/> pooling ceiling (~2^20 elements), be
/// allocated straight on the Large Object Heap and never pooled. Splitting the store into fixed 256-element
/// segments keeps every allocation small enough to stay off the LOH and lets segments be rented and returned
/// granularly. The list is build-once/overwrite-from-zero: <see cref="EnsureCapacity"/> only ever adds segments
/// and never copies or preserves element contents, matching how <see cref="SortedMergeDictionary{TKey,TValue}"/>
/// rebuilds from index 0 on every build.
/// </remarks>
/// <param name="clearOnReturn">
/// Whether segments are cleared when returned to the pool — <c>true</c> for element types holding managed
/// references (so they aren't pinned), <c>false</c> for pure value types.
/// </param>
internal sealed class SegmentedList<T>(bool clearOnReturn) : IDisposable
{
    private const int SegmentSize = 256;
    private const int SegmentShift = 8;
    private const int SegmentMask = SegmentSize - 1;

    private T[][] _segments = [];
    private int _segmentCount;

    /// <summary>The number of addressable elements the currently allocated segments can hold.</summary>
    public int Capacity => _segmentCount << SegmentShift;

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _segments[index >> SegmentShift][index & SegmentMask];
    }

    /// <summary>
    /// Ensures at least <paramref name="count"/> elements are addressable, renting and clearing new segments as
    /// needed. Existing segments (and their cleared tails) are retained; no element content is copied.
    /// </summary>
    public void EnsureCapacity(int count)
    {
        int needed = (count + SegmentMask) >> SegmentShift;
        if (needed <= _segmentCount) return;

        if (needed > _segments.Length)
        {
            int newLength = _segments.Length == 0 ? needed : _segments.Length;
            while (newLength < needed) newLength <<= 1;
            T[][] grown = new T[newLength][];
            Array.Copy(_segments, grown, _segmentCount);
            _segments = grown;
        }

        for (int i = _segmentCount; i < needed; i++)
        {
            T[] segment = ArrayPool<T>.Shared.Rent(SegmentSize);
            // Rented segments aren't zeroed; only the first SegmentSize slots are ever addressed.
            Array.Clear(segment, 0, SegmentSize);
            _segments[i] = segment;
        }

        _segmentCount = needed;
    }

    /// <summary>Clears the first <paramref name="length"/> elements, spanning segment boundaries.</summary>
    public void Clear(int length)
    {
        int remaining = length;
        for (int i = 0; remaining > 0; i++)
        {
            int toClear = remaining < SegmentSize ? remaining : SegmentSize;
            Array.Clear(_segments[i], 0, toClear);
            remaining -= toClear;
        }
    }

    /// <summary>Sorts the first <paramref name="count"/> elements in place using <paramref name="comparer"/>.</summary>
    /// <remarks>
    /// In-place heapsort over the segment indexer: guaranteed O(n log n) with no scratch buffer, which suits the
    /// snapshot-sized inputs this runs on. If it ever shows up as a hot path, the faster replacement is a
    /// per-segment <see cref="System.MemoryExtensions.Sort{T}(Span{T})"/> followed by a bottom-up merge of the
    /// sorted runs into a scratch list.
    /// </remarks>
    public void Sort(int count, IComparer<T> comparer)
    {
        if (count <= 1) return;

        for (int start = (count >> 1) - 1; start >= 0; start--) SiftDown(start, count, comparer);

        for (int end = count - 1; end > 0; end--)
        {
            (this[end], this[0]) = (this[0], this[end]);
            SiftDown(0, end, comparer);
        }
    }

    private void SiftDown(int start, int end, IComparer<T> comparer)
    {
        int root = start;
        while (true)
        {
            int child = (root << 1) + 1;
            if (child >= end) break;
            if (child + 1 < end && comparer.Compare(this[child], this[child + 1]) < 0) child++;
            if (comparer.Compare(this[root], this[child]) >= 0) break;

            (this[root], this[child]) = (this[child], this[root]);
            root = child;
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < _segmentCount; i++)
        {
            ArrayPool<T>.Shared.Return(_segments[i], clearOnReturn);
        }

        _segments = [];
        _segmentCount = 0;
    }
}
