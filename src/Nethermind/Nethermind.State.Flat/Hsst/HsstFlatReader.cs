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
    /// Parsed footer of a FlatEntries HSST: section starts/ends, stride, and per-level
    /// summary offsets.
    /// </summary>
    internal ref struct Layout
    {
        public long DataStart;
        public int KeySize;
        public int ValueSize;
        public int EntryCount;
        public long HashTableStart;
        public int HashTableSize;
        public int Depth;
        // Inline arrays sized to MaxSummaryDepth. Only [0..Depth) are valid.
        public InlineLevelArray LevelStarts;
        public InlineLevelArray LevelCounts;

        public int EntryStride => KeySize + ValueSize;
        public int CheckpointEntrySize => KeySize + 4;
        public long EntryAbsStart(int entryIdx) => DataStart + (long)entryIdx * EntryStride;
        public long ValueAbsStart(int entryIdx) => EntryAbsStart(entryIdx) + KeySize;
    }

    [System.Runtime.CompilerServices.InlineArray(HsstFlatLayout.MaxSummaryDepth)]
    internal struct InlineLevelArray
    {
        private long _e0;
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

        Span<byte> metaBuf = stackalloc byte[256];
        if (metaLen > metaBuf.Length) return false;
        if (!reader.TryRead(metaAbsStart, metaBuf[..metaLen])) return false;
        int p = 0;
        int keySize = Leb128.Read(metaBuf, ref p);
        int valueSize = Leb128.Read(metaBuf, ref p);
        int entryCount = Leb128.Read(metaBuf, ref p);
        int tableSize = Leb128.Read(metaBuf, ref p);
        int depth = Leb128.Read(metaBuf, ref p);
        if (keySize < 0 || valueSize < 0 || entryCount < 0 || tableSize < 0 || depth < 0) return false;
        if (keySize > 255) return false;
        if (depth > HsstFlatLayout.MaxSummaryDepth) return false;

        layout.KeySize = keySize;
        layout.ValueSize = valueSize;
        layout.EntryCount = entryCount;
        layout.HashTableSize = tableSize;
        layout.Depth = depth;

        // Read per-level counts.
        Span<int> counts = stackalloc int[HsstFlatLayout.MaxSummaryDepth];
        for (int i = 0; i < depth; i++)
        {
            int c = Leb128.Read(metaBuf, ref p);
            if (c < 0) return false;
            counts[i] = c;
            layout.LevelCounts[i] = c;
        }

        long hashTableEnd = metaAbsStart;
        long hashTableBytes = (long)tableSize * 4;
        long hashTableStart = hashTableEnd - hashTableBytes;
        if (hashTableStart < hsstStart) return false;
        layout.HashTableStart = hashTableStart;

        // Summaries lie before the hash table (or before metadata when there's no hash
        // table). Level (Depth-1) is closest to the hash table; Level 0 is closest to Data.
        long cursor = hashTableStart;
        // Walk backward: level (Depth-1) is closest to the hash table; level 0 is closest to Data.
        int entrySize = keySize + 4;
        for (int lvl = depth - 1; lvl >= 0; lvl--)
        {
            long lvlBytes = (long)counts[lvl] * entrySize;
            long lvlStart = cursor - lvlBytes;
            if (lvlStart < hsstStart) return false;
            layout.LevelStarts[lvl] = lvlStart;
            cursor = lvlStart;
        }

        // Data ends where level 0 begins (or where the hash table begins, when depth == 0).
        long dataBytes = (long)entryCount * (keySize + valueSize);
        if (hsstStart + dataBytes != cursor) return false;
        layout.DataStart = hsstStart;

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

        // Hash fast path applies only to keys of the right length and when a table is present.
        if (key.Length == L.KeySize && L.HashTableSize > 0)
        {
            uint h = HsstHash.HashKey(key);
            uint slot = HsstHash.Slot(h, L.HashTableSize);
            Span<byte> slotBuf = stackalloc byte[4];
            if (!reader.TryRead(L.HashTableStart + slot * 4, slotBuf)) return false;
            uint slotValue = BinaryPrimitives.ReadUInt32LittleEndian(slotBuf);

            const uint Empty = 0u;
            const uint Collision = 0xFFFFFFFFu;

            if (slotValue == Empty)
            {
                if (exactMatch) return false;
                // Floor: fall through to summary descent.
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

        // Recursive summary descent: at each level k from top to 0, find the smallest
        // checkpoint with key >= target, then narrow the search range at level k-1 (or in
        // Data when k == 0) to the slab covered by that checkpoint.
        int rangeStart;
        int rangeEnd;

        if (L.Depth == 0)
        {
            // No summary at all — search the whole Data range.
            rangeStart = 0;
            rangeEnd = L.EntryCount - 1;
        }
        else
        {
            // Start at the top level with full range.
            int levelLo = 0;
            int levelHi = (int)L.LevelCounts[L.Depth - 1] - 1;

            // Walk levels top-down. At each level we narrow [levelLo, levelHi]; when we drop
            // to the next level down we read the chosen checkpoint's LastEntryIndex bounds.
            for (int lvl = L.Depth - 1; lvl >= 0; lvl--)
            {
                long lvlStart = L.LevelStarts[lvl];
                int ckIdx = SearchSummaryLevel<TReader, TPin>(
                    in reader, lvlStart, L.KeySize, levelLo, levelHi + 1, key, out bool readOk);
                if (!readOk) return false;

                if (ckIdx > levelHi)
                {
                    // Target greater than every checkpoint in this slab.
                    if (exactMatch) return false;
                    if (lvl == 0)
                    {
                        // Floor: largest entry overall in the slab — but since we exhausted
                        // this slab's level-0 checkpoints, the floor is the last data entry
                        // covered by this slab. Use the last checkpoint's LastEntryIndex.
                        if (!ReadCheckpointEntryIdx<TReader, TPin>(in reader, lvlStart, L.KeySize, levelHi, out int last)) return false;
                        resultBound = new Bound(L.ValueAbsStart(last), L.ValueSize);
                        return true;
                    }
                    // For non-leaf summary levels, "off the end" means the target is greater
                    // than every key in the slab; the floor lives in the last child slab.
                    ckIdx = levelHi;
                }

                // Compute the slab at the next level down: [prev.LastEntryIndex+1, ck.LastEntryIndex].
                if (!ReadCheckpointEntryIdx<TReader, TPin>(in reader, lvlStart, L.KeySize, ckIdx, out int newHi)) return false;
                int newLo;
                if (ckIdx == 0)
                {
                    newLo = 0;
                }
                else
                {
                    if (!ReadCheckpointEntryIdx<TReader, TPin>(in reader, lvlStart, L.KeySize, ckIdx - 1, out int prev)) return false;
                    newLo = prev + 1;
                }

                if (lvl == 0)
                {
                    rangeStart = newLo;
                    rangeEnd = newHi;
                    goto finish;
                }
                levelLo = newLo;
                levelHi = newHi;
            }
            // Should be unreachable given the goto above.
            return false;
        }

    finish:
        // Binary search within [rangeStart, rangeEnd] inclusive in Data for the smallest
        // entry whose key is >= target.
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

        // Floor: take the previous entry (in absolute index space). Range boundaries don't
        // matter — the entry array is globally sorted.
        int floorIdx = lo - 1;
        if (floorIdx < 0) return false;
        resultBound = new Bound(L.ValueAbsStart(floorIdx), L.ValueSize);
        return true;
    }

    /// <summary>
    /// Binary-search a summary level slab `[lo, hi)` for the smallest checkpoint whose key
    /// is &gt;= <paramref name="key"/>. Returns <c>hi</c> when no such checkpoint exists.
    /// </summary>
    private static int SearchSummaryLevel<TReader, TPin>(
        scoped in TReader reader, long levelStart, int keySize,
        int lo, int hi, scoped ReadOnlySpan<byte> key, out bool readOk)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        readOk = true;
        Span<byte> ckBuf = stackalloc byte[255];
        Span<byte> ckSlice = ckBuf[..keySize];
        int entrySize = keySize + 4;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            long ckEntryStart = levelStart + (long)mid * entrySize;
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
        scoped in TReader reader, long levelStart, int keySize, int ckIdx, out int entryIdx)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        entryIdx = 0;
        Span<byte> idxBuf = stackalloc byte[4];
        long off = levelStart + (long)ckIdx * (keySize + 4) + keySize;
        if (!reader.TryRead(off, idxBuf)) return false;
        entryIdx = BinaryPrimitives.ReadInt32LittleEndian(idxBuf);
        return true;
    }
}
