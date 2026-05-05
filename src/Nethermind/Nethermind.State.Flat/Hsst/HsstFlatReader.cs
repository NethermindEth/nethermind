// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Read-side helpers for the <see cref="IndexType.FlatEntries"/> layout. Stateless static
/// methods so <see cref="HsstReader{TReader,TPin}"/> can dispatch into them without copying
/// its ref-struct state.
/// </summary>
internal static class HsstFlatReader
{
    /// <summary>
    /// Parsed footer of a FlatEntries HSST: section starts/ends and the entry stride.
    /// All offsets are absolute reader positions.
    /// </summary>
    internal readonly struct Layout(
        long dataStart,
        int keySize,
        int valueSize,
        int entryCount,
        long binaryIndexStart,
        int indexCount,
        long hashTableStart,
        int hashLog2)
    {
        public readonly long DataStart = dataStart;
        public readonly int KeySize = keySize;
        public readonly int ValueSize = valueSize;
        public readonly int EntryCount = entryCount;
        public readonly long BinaryIndexStart = binaryIndexStart;
        public readonly int IndexCount = indexCount;
        public readonly long HashTableStart = hashTableStart;
        public readonly int HashLog2 = hashLog2;

        public int EntryStride => KeySize + ValueSize;
        public int CheckpointEntrySize => KeySize + 4;
        public long EntryAbsStart(int entryIdx) => DataStart + (long)entryIdx * EntryStride;
        public long ValueAbsStart(int entryIdx) => EntryAbsStart(entryIdx) + KeySize;
    }

    /// <summary>
    /// Parse the FlatEntries footer. Returns false on truncation or self-inconsistency.
    /// </summary>
    public static bool TryReadLayout<TReader, TPin>(scoped in TReader reader, Bound bound, out Layout layout)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        layout = default;
        long hsstStart = bound.Offset;
        long hsstEnd = bound.Offset + bound.Length;

        // [Metadata][MetadataLength: u8][IndexType: u8].
        if (bound.Length < 3) return false;
        Span<byte> oneByte = stackalloc byte[1];
        if (!reader.TryRead(hsstEnd - 2, oneByte)) return false;
        int metaLen = oneByte[0];
        long metaAbsStart = hsstEnd - 2 - metaLen;
        if (metaAbsStart < hsstStart) return false;

        Span<byte> metaBuf = stackalloc byte[64];
        if (metaLen > metaBuf.Length) return false;
        if (!reader.TryRead(metaAbsStart, metaBuf[..metaLen])) return false;
        int p = 0;
        int keySize = Leb128.Read(metaBuf, ref p);
        int valueSize = Leb128.Read(metaBuf, ref p);
        int entryCount = Leb128.Read(metaBuf, ref p);
        int indexCount = Leb128.Read(metaBuf, ref p);
        if (keySize < 0 || valueSize < 0 || entryCount < 0 || indexCount < 0) return false;
        if (keySize > 255) return false;

        // TableSizeLog2 sits one byte before metadata.
        if (!reader.TryRead(metaAbsStart - 1, oneByte)) return false;
        int log2 = oneByte[0];
        if (log2 > 31) return false;
        long tableSize = 1L << log2;
        long tableBytes = tableSize * 4;
        long hashTableStart = metaAbsStart - 1 - tableBytes;
        if (hashTableStart < hsstStart) return false;

        long binaryIndexBytes = (long)indexCount * (keySize + 4);
        long binaryIndexStart = hashTableStart - binaryIndexBytes;
        if (binaryIndexStart < hsstStart) return false;

        long dataBytes = (long)entryCount * (keySize + valueSize);
        if (hsstStart + dataBytes != binaryIndexStart) return false;

        layout = new Layout(hsstStart, keySize, valueSize, entryCount,
            binaryIndexStart, indexCount, hashTableStart, log2);
        return true;
    }

    /// <summary>
    /// Exact-match or floor lookup over a FlatEntries HSST. On success sets
    /// <paramref name="resultBound"/> to the value region of the matched entry.
    /// </summary>
    public static bool TrySeek<TReader, TPin>(
        scoped in TReader reader, Bound bound, scoped ReadOnlySpan<byte> key,
        bool exactMatch, out Bound resultBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        resultBound = default;
        if (!TryReadLayout<TReader, TPin>(in reader, bound, out Layout L))
            return false;

        if (L.EntryCount == 0) return false;

        // Hash fast path applies only to keys of the right length. For floor lookups with
        // mismatched length we still need the b-search through the binary index.
        if (key.Length == L.KeySize && L.HashLog2 >= 0)
        {
            uint h = HsstHash.HashKey(key);
            uint mask = (uint)((1L << L.HashLog2) - 1);
            uint slot = h & mask;
            Span<byte> slotBuf = stackalloc byte[4];
            if (!reader.TryRead(L.HashTableStart + slot * 4, slotBuf)) return false;
            uint slotValue = BinaryPrimitives.ReadUInt32LittleEndian(slotBuf);

            const uint Empty = 0u;
            const uint Collision = 0xFFFFFFFFu;

            if (slotValue == Empty)
            {
                if (exactMatch) return false;
                // Floor: fall through to binary search.
            }
            else if (slotValue != Collision)
            {
                int entryIdx = (int)(slotValue - 1);
                if ((uint)entryIdx >= (uint)L.EntryCount) return false;
                Span<byte> stored = stackalloc byte[255];
                Span<byte> storedSlice = stored[..L.KeySize];
                if (!reader.TryRead(L.EntryAbsStart(entryIdx), storedSlice)) return false;
                if (storedSlice.SequenceEqual(key))
                {
                    resultBound = new Bound(L.ValueAbsStart(entryIdx), L.ValueSize);
                    return true;
                }
                if (exactMatch) return false;
                // Floor: fall through.
            }
            // Collision sentinel: fall through.
        }

        // Binary index: find the smallest checkpoint with key >= target.
        // The search is over `IndexCount` entries; each compare reads `KeySize` bytes.
        int ckIdx = SearchBinaryIndex<TReader, TPin>(in reader, L, key, out bool ckReadOk);
        if (!ckReadOk) return false;

        int rangeStart;
        int rangeEnd;
        if (ckIdx == L.IndexCount)
        {
            // Target is greater than every checkpoint key -> no entry matches.
            if (exactMatch) return false;
            // Floor: largest entry overall.
            resultBound = new Bound(L.ValueAbsStart(L.EntryCount - 1), L.ValueSize);
            return true;
        }
        if (ckIdx == 0)
        {
            rangeStart = 0;
        }
        else
        {
            if (!ReadCheckpointEntryIdx<TReader, TPin>(in reader, L, ckIdx - 1, out int prev)) return false;
            rangeStart = prev + 1;
        }
        if (!ReadCheckpointEntryIdx<TReader, TPin>(in reader, L, ckIdx, out int last)) return false;
        rangeEnd = last;

        // Binary search within [rangeStart, rangeEnd] inclusive for the smallest entry whose
        // key is >= target.
        int lo = rangeStart;
        int hi = rangeEnd + 1;
        Span<byte> stored2 = stackalloc byte[255];
        Span<byte> storedSlice2 = stored2[..L.KeySize];
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (!reader.TryRead(L.EntryAbsStart(mid), storedSlice2)) return false;
            if (storedSlice2.SequenceCompareTo(key) < 0) lo = mid + 1;
            else hi = mid;
        }
        // lo is the insertion index. If lo points at an entry whose key equals target -> hit.
        if (lo <= rangeEnd)
        {
            if (!reader.TryRead(L.EntryAbsStart(lo), storedSlice2)) return false;
            if (storedSlice2.SequenceEqual(key))
            {
                resultBound = new Bound(L.ValueAbsStart(lo), L.ValueSize);
                return true;
            }
        }
        if (exactMatch) return false;

        // Floor: take the previous entry (in absolute index space). Range boundaries don't
        // matter — the entry array is globally sorted.
        int floorIdx = lo - 1;
        if (floorIdx < 0) return false;
        resultBound = new Bound(L.ValueAbsStart(floorIdx), L.ValueSize);
        return true;
    }

    /// <summary>
    /// Binary-search the binary-index section for the smallest checkpoint whose key is &gt;=
    /// <paramref name="key"/>. Returns <c>IndexCount</c> when no such checkpoint exists.
    /// </summary>
    private static int SearchBinaryIndex<TReader, TPin>(
        scoped in TReader reader, Layout L, scoped ReadOnlySpan<byte> key, out bool readOk)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        readOk = true;
        int lo = 0, hi = L.IndexCount;
        Span<byte> ckBuf = stackalloc byte[255];
        Span<byte> ckSlice = ckBuf[..L.KeySize];
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            long ckEntryStart = L.BinaryIndexStart + (long)mid * L.CheckpointEntrySize;
            if (!reader.TryRead(ckEntryStart, ckSlice))
            {
                readOk = false;
                return 0;
            }
            if (ckSlice.SequenceCompareTo(key) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static bool ReadCheckpointEntryIdx<TReader, TPin>(
        scoped in TReader reader, Layout L, int ckIdx, out int entryIdx)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        entryIdx = 0;
        Span<byte> idxBuf = stackalloc byte[4];
        long off = L.BinaryIndexStart + (long)ckIdx * L.CheckpointEntrySize + L.KeySize;
        if (!reader.TryRead(off, idxBuf)) return false;
        entryIdx = BinaryPrimitives.ReadInt32LittleEndian(idxBuf);
        return true;
    }
}
