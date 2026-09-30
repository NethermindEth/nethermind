// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;

namespace Nethermind.Pbt;

/// <summary>Sorts write operations by key.</summary>
/// <remarks>
/// Keys are compared eight bytes at a time as big-endian integers held in a compact (chunk, index) array, so the sort
/// moves 16-byte entries rather than whole operations. Runs agreeing on a chunk are refined by the next one, and short
/// runs by full-key insertion sort; the operations are then permuted into place once, without a second batch buffer.
/// </remarks>
internal static class PbtOperationSort
{
    private const int InsertionSortThreshold = 16;

    internal static void Sort<TKey>(Span<PbtWriteOperation<TKey>> operations) where TKey : unmanaged, IPbtKey<TKey>
    {
        if (operations.Length < 2) return;
        Entry[] entries = ArrayPool<Entry>.Shared.Rent(operations.Length);
        try
        {
            Span<Entry> span = entries.AsSpan(0, operations.Length);
            for (int index = 0; index < span.Length; index++) span[index] = new(Chunk(operations[index].Key, 0), index);
            SortByChunk(operations, span, 0);
            Permute(operations, span);
        }
        finally
        {
            ArrayPool<Entry>.Shared.Return(entries);
        }
    }

    /// <summary>Orders <paramref name="run"/> by the chunks at <paramref name="offset"/>, then refines every tie by the next chunk.</summary>
    private static void SortByChunk<TKey>(ReadOnlySpan<PbtWriteOperation<TKey>> operations, Span<Entry> run, int offset) where TKey : unmanaged, IPbtKey<TKey>
    {
        run.Sort();
        for (int start = 0; start < run.Length;)
        {
            int end = start + 1;
            while (end < run.Length && run[end].Chunk == run[start].Chunk) end++;
            if (end - start > 1) Refine(operations, run[start..end], offset + sizeof(ulong));
            start = end;
        }
    }

    /// <summary>Orders a run whose keys agree on every byte before <paramref name="offset"/>.</summary>
    private static void Refine<TKey>(ReadOnlySpan<PbtWriteOperation<TKey>> operations, Span<Entry> run, int offset) where TKey : unmanaged, IPbtKey<TKey>
    {
        // Past the last byte only a variable-length key can still differ, by length, which the full comparison settles.
        if (run.Length <= InsertionSortThreshold || offset >= TKey.Capacity)
        {
            InsertionSort(operations, run);
            return;
        }
        foreach (ref Entry entry in run) entry = new(Chunk(operations[entry.Index].Key, offset), entry.Index);
        SortByChunk(operations, run, offset);
    }

    private static void InsertionSort<TKey>(ReadOnlySpan<PbtWriteOperation<TKey>> operations, Span<Entry> run) where TKey : unmanaged, IPbtKey<TKey>
    {
        for (int index = 1; index < run.Length; index++)
        {
            Entry entry = run[index];
            // Keys are copied out first: a span taken off the property would point at an unnamed temporary.
            TKey key = operations[entry.Index].Key;
            int position = index - 1;
            for (; position >= 0; position--)
            {
                TKey other = operations[run[position].Index].Key;
                if (other.CompareTo(key) <= 0) break;
                run[position + 1] = run[position];
            }
            run[position + 1] = entry;
        }
    }

    /// <summary>The eight key bytes at <paramref name="offset"/> as a big-endian integer, zero-padded past the key's end.</summary>
    private static ulong Chunk<TKey>(TKey key, int offset) where TKey : unmanaged, IPbtKey<TKey>
    {
        ReadOnlySpan<byte> bytes = key.Bytes;
        if (offset + sizeof(ulong) <= bytes.Length) return BinaryPrimitives.ReadUInt64BigEndian(bytes[offset..]);
        Span<byte> padded = stackalloc byte[sizeof(ulong)];
        padded.Clear();
        if (offset < bytes.Length) bytes[offset..].CopyTo(padded);
        return BinaryPrimitives.ReadUInt64BigEndian(padded);
    }

    /// <summary>Moves every operation to its sorted position by following cycles, complementing each placed entry's index.</summary>
    private static void Permute<TKey>(Span<PbtWriteOperation<TKey>> operations, Span<Entry> sorted) where TKey : unmanaged, IPbtKey<TKey>
    {
        for (int start = 0; start < sorted.Length; start++)
        {
            int source = sorted[start].Index;
            if (source < 0 || source == start) continue;
            PbtWriteOperation<TKey> held = operations[start];
            int target = start;
            while (source != start)
            {
                operations[target] = operations[source];
                sorted[target] = new(0, ~source);
                target = source;
                source = sorted[target].Index;
            }
            operations[target] = held;
            sorted[target] = new(0, ~source);
        }
    }

    private readonly struct Entry(ulong chunk, int index) : IComparable<Entry>
    {
        internal readonly ulong Chunk = chunk;
        internal readonly int Index = index;
        public int CompareTo(Entry other) => Chunk.CompareTo(other.Chunk);
    }
}
