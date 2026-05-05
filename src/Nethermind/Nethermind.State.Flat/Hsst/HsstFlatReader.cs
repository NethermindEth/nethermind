// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.BSearchIndex;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Read-side helpers for the <see cref="IndexType.FlatEntries"/> layout. Stateless static
/// methods so <see cref="HsstReader{TReader,TPin}"/> can dispatch into them without copying
/// its ref-struct state.
/// </summary>
internal static class HsstFlatReader
{
    /// <summary>
    /// Parsed footer of a FlatEntries HSST: section starts and per-level summary geometry.
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
        public int EntriesPerCkLevel0Log2;
        public int RecordsPerCkHigherLog2;
        // Inline arrays sized to MaxSummaryDepth. Only [0..Depth) are valid.
        public InlineLevelArray LevelStarts;
        public InlineLevelArray LevelCounts;

        public int EntryStride => KeySize + ValueSize;
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
        int entriesPerCk0Log2 = Leb128.Read(metaBuf, ref p);
        int recordsPerCkHigherLog2 = Leb128.Read(metaBuf, ref p);
        int depth = Leb128.Read(metaBuf, ref p);
        if (keySize < 0 || valueSize < 0 || entryCount < 0 || tableSize < 0 ||
            entriesPerCk0Log2 < 0 || recordsPerCkHigherLog2 < 0 || depth < 0) return false;
        if (keySize > 255) return false;
        if (depth > HsstFlatLayout.MaxSummaryDepth) return false;
        // Clamp shifts to a safe range — bigger than 30 would overflow int slab arithmetic.
        if (entriesPerCk0Log2 > 30 || recordsPerCkHigherLog2 > 30) return false;
        if (depth >= 2 && recordsPerCkHigherLog2 < 1) return false;

        layout.KeySize = keySize;
        layout.ValueSize = valueSize;
        layout.EntryCount = entryCount;
        layout.HashTableSize = tableSize;
        layout.Depth = depth;
        layout.EntriesPerCkLevel0Log2 = entriesPerCk0Log2;
        layout.RecordsPerCkHigherLog2 = recordsPerCkHigherLog2;

        Span<int> counts = stackalloc int[HsstFlatLayout.MaxSummaryDepth];
        for (int i = 0; i < depth; i++)
        {
            int c = Leb128.Read(metaBuf, ref p);
            if (c <= 0) return false;
            counts[i] = c;
            layout.LevelCounts[i] = c;
        }

        long hashTableEnd = metaAbsStart;
        long hashTableBytes = (long)tableSize * 4;
        long hashTableStart = hashTableEnd - hashTableBytes;
        if (hashTableStart < hsstStart) return false;
        layout.HashTableStart = hashTableStart;

        // Summaries lie before the hash table. Each record is exactly KeySize bytes.
        long cursor = hashTableStart;
        for (int lvl = depth - 1; lvl >= 0; lvl--)
        {
            long lvlBytes = (long)counts[lvl] * keySize;
            long lvlStart = cursor - lvlBytes;
            if (lvlStart < hsstStart) return false;
            layout.LevelStarts[lvl] = lvlStart;
            cursor = lvlStart;
        }

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

        // Recursive summary descent. At each level k, the active slab is [levelLo, levelHi]
        // (closed). Find the smallest ck c with key >= target in that slab; if none, take
        // c = levelHi for floor (covers the last child slab). Slab semantics:
        //   stride = (k == 0) ? EntriesPerCkLevel0 : RecordsPerCkHigher
        //   parentCount = (k == 0) ? EntryCount : Count_{k-1}
        //   childSlab = [c*stride, min((c+1)*stride - 1, parentCount - 1)]
        int rangeStart;
        int rangeEnd;

        if (L.Depth == 0)
        {
            rangeStart = 0;
            rangeEnd = L.EntryCount - 1;
        }
        else
        {
            int levelLo = 0;
            int levelHi = (int)L.LevelCounts[L.Depth - 1] - 1;
            int curLvl = L.Depth - 1;
            rangeStart = 0;
            rangeEnd = -1;
            while (true)
            {
                int ckIdx = SearchSummaryLevel<TReader, TPin>(
                    in reader, L.LevelStarts[curLvl], L.KeySize, levelLo, levelHi + 1, key, out bool readOk);
                if (!readOk) return false;

                if (ckIdx > levelHi)
                {
                    if (exactMatch) return false;
                    ckIdx = levelHi;
                }

                int strideLog2 = (curLvl == 0) ? L.EntriesPerCkLevel0Log2 : L.RecordsPerCkHigherLog2;
                int parentCount = (curLvl == 0) ? L.EntryCount : (int)L.LevelCounts[curLvl - 1];
                int newLo = ckIdx << strideLog2;
                int newHi = Math.Min(((ckIdx + 1) << strideLog2) - 1, parentCount - 1);

                if (curLvl == 0)
                {
                    rangeStart = newLo;
                    rangeEnd = newHi;
                    break;
                }
                levelLo = newLo;
                levelHi = newHi;
                curLvl--;
            }
        }

        // Binary search [rangeStart, rangeEnd] in Data for the smallest entry whose key
        // is >= target.
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
    /// Each summary record is exactly <paramref name="keySize"/> bytes (no trailing index).
    /// </summary>
    private static int SearchSummaryLevel<TReader, TPin>(
        scoped in TReader reader, long levelStart, int keySize,
        int lo, int hi, scoped ReadOnlySpan<byte> key, out bool readOk)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        readOk = true;

        // SIMD fast path: packed fixed-width 4- or 8-byte keys, slab small enough to
        // scan linearly. Reuses BSearchIndexReaderSimd's enable flag and stripe cap so
        // this path tunes together with the b-tree intermediate-node path.
        if (BSearchIndexReaderSimd.Enabled && (keySize == 4 || keySize == 8) && key.Length == keySize)
        {
            int n = hi - lo;
            if (n >= 2 && n <= BSearchIndexReaderSimd.LinearScanMaxCount)
            {
                long slabAbsStart = levelStart + (long)lo * keySize;
                int slabBytes = n * keySize;
                using TPin slabPin = reader.PinBuffer(slabAbsStart, slabBytes);
                ReadOnlySpan<byte> slab = slabPin.Buffer;
                if (BSearchIndexReaderSimd.TryFindFloorIndexUniformSimd(
                        key, slab, n, keySize, out int floor))
                {
                    if (floor < 0) return lo;
                    ReadOnlySpan<byte> floorKey = slab.Slice(floor * keySize, keySize);
                    if (floorKey.SequenceEqual(key)) return lo + floor;
                    // SIMD floor invariant: slab[floor] < key (strict). Ceiling is
                    // floor + 1, which equals hi when floor == n - 1 (no key >= target).
                    return lo + floor + 1;
                }
            }
        }

        Span<byte> ckBuf = stackalloc byte[255];
        Span<byte> ckSlice = ckBuf[..keySize];
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            long ckEntryStart = levelStart + (long)mid * keySize;
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
}
