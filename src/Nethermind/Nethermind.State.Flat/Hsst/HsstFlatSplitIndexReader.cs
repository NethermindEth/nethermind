// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Read-side helpers for the <see cref="IndexType.FlatEntriesSplitIndex"/> layout. Same as
/// <see cref="HsstFlatReader"/>, except that the binary index is split: checkpoint keys live
/// in one contiguous slab followed by the checkpoint entry indices in another.
/// </summary>
internal static class HsstFlatSplitIndexReader
{
    /// <summary>
    /// Parsed footer of a FlatEntriesSplitIndex HSST. <see cref="CheckpointKeysStart"/> is
    /// the absolute offset of the first checkpoint key; <see cref="CheckpointValuesStart"/>
    /// is the absolute offset of the first 4-byte checkpoint entry index.
    /// </summary>
    internal readonly struct Layout(
        long dataStart,
        int keySize,
        int valueSize,
        int entryCount,
        long checkpointKeysStart,
        long checkpointValuesStart,
        int indexCount,
        long hashTableStart,
        int hashLog2)
    {
        public readonly long DataStart = dataStart;
        public readonly int KeySize = keySize;
        public readonly int ValueSize = valueSize;
        public readonly int EntryCount = entryCount;
        public readonly long CheckpointKeysStart = checkpointKeysStart;
        public readonly long CheckpointValuesStart = checkpointValuesStart;
        public readonly int IndexCount = indexCount;
        public readonly long HashTableStart = hashTableStart;
        public readonly int HashLog2 = hashLog2;

        public int EntryStride => KeySize + ValueSize;
        public long EntryAbsStart(int entryIdx) => DataStart + (long)entryIdx * EntryStride;
        public long ValueAbsStart(int entryIdx) => EntryAbsStart(entryIdx) + KeySize;
    }

    public static bool TryReadLayout<TReader, TPin>(scoped in TReader reader, Bound bound, out Layout layout)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        layout = default;
        long hsstStart = bound.Offset;
        long hsstEnd = bound.Offset + bound.Length;

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

        if (!reader.TryRead(metaAbsStart - 1, oneByte)) return false;
        int log2 = oneByte[0];
        if (log2 > 31) return false;
        long tableSize = 1L << log2;
        long tableBytes = tableSize * 4;
        long hashTableStart = metaAbsStart - 1 - tableBytes;
        if (hashTableStart < hsstStart) return false;

        long ckValuesBytes = (long)indexCount * 4;
        long ckValuesStart = hashTableStart - ckValuesBytes;
        if (ckValuesStart < hsstStart) return false;

        long ckKeysBytes = (long)indexCount * keySize;
        long ckKeysStart = ckValuesStart - ckKeysBytes;
        if (ckKeysStart < hsstStart) return false;

        long dataBytes = (long)entryCount * (keySize + valueSize);
        if (hsstStart + dataBytes != ckKeysStart) return false;

        layout = new Layout(hsstStart, keySize, valueSize, entryCount,
            ckKeysStart, ckValuesStart, indexCount, hashTableStart, log2);
        return true;
    }

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
            }
        }

        int ckIdx = SearchBinaryIndex<TReader, TPin>(in reader, L, key, out bool ckReadOk);
        if (!ckReadOk) return false;

        int rangeStart;
        int rangeEnd;
        if (ckIdx == L.IndexCount)
        {
            if (exactMatch) return false;
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

        int floorIdx = lo - 1;
        if (floorIdx < 0) return false;
        resultBound = new Bound(L.ValueAbsStart(floorIdx), L.ValueSize);
        return true;
    }

    /// <summary>
    /// Binary-search the contiguous checkpoint-key slab for the smallest checkpoint whose key
    /// is &gt;= <paramref name="key"/>. Returns <c>IndexCount</c> if no such checkpoint exists.
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
            long ckKeyStart = L.CheckpointKeysStart + (long)mid * L.KeySize;
            if (!reader.TryRead(ckKeyStart, ckSlice))
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
        long off = L.CheckpointValuesStart + (long)ckIdx * 4;
        if (!reader.TryRead(off, idxBuf)) return false;
        entryIdx = BinaryPrimitives.ReadInt32LittleEndian(idxBuf);
        return true;
    }
}
