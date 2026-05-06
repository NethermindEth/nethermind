// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Read-side helpers for the <see cref="IndexType.PackedArrayVariableValue"/> layout.
/// Stateless static methods so <see cref="HsstReader{TReader,TPin}"/> can dispatch into
/// them without copying its ref-struct state.
/// </summary>
internal static class HsstPackedArrayVariableValueReader
{
    /// <summary>
    /// Parsed footer of a PackedArrayVariableValue HSST: section starts and per-level
    /// summary geometry. <see cref="LevelStarts"/> entries are int offsets relative to
    /// <see cref="HsstStart"/>.
    /// </summary>
    internal ref struct Layout
    {
        public long HsstStart;
        public long HsstEnd;
        public int KeySize;
        public int EntryCount;
        public int EntriesByteLen;
        public long EntryMetaStartsStart;     // = HsstStart + EntriesByteLen
        public long HashTableStart;
        public int HashTableSize;
        public int Depth;
        public int EntriesPerCkLevel0Log2;
        public int RecordsPerCkHigherLog2;
        public HsstPackedArrayReader.InlineLevelArray LevelStarts;
        public HsstPackedArrayReader.InlineLevelArray LevelCounts;

        public long LevelAbsStart(int level) => HsstStart + (uint)LevelStarts[level];
        public long EntryMetaStartAbs(int entryIdx) => EntryMetaStartsStart + (long)entryIdx * 4;
    }

    /// <summary>
    /// Tail window pinned by <see cref="TryReadLayout"/>. Sized to fit every metadata
    /// block emitted by the current builder so the common case completes with a single pin.
    /// </summary>
    private const int TailWindowSize = 64;

    public static bool TryReadLayout<TReader, TPin>(scoped in TReader reader, Bound bound, out Layout layout)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        layout = default;
        long hsstStart = bound.Offset;
        long hsstEnd = bound.Offset + bound.Length;

        if (bound.Length < 3) return false;

        int tailLen = (int)Math.Min(TailWindowSize, bound.Length);
        long tailAbsStart = hsstEnd - tailLen;

        int metaLen;
        long metaAbsStart;

        using (TPin tailPin = reader.PinBuffer(tailAbsStart, tailLen))
        {
            ReadOnlySpan<byte> tail = tailPin.Buffer;
            metaLen = tail[tailLen - 2];
            metaAbsStart = hsstEnd - 2 - metaLen;
            if (metaAbsStart < hsstStart) return false;

            if (metaLen + 2 <= tailLen)
            {
                ReadOnlySpan<byte> metaSpan = tail.Slice(tailLen - 2 - metaLen, metaLen);
                return ParseMetadata(metaSpan, hsstStart, hsstEnd, metaAbsStart, ref layout);
            }
        }

        using (TPin metaPin = reader.PinBuffer(metaAbsStart, metaLen))
        {
            return ParseMetadata(metaPin.Buffer, hsstStart, hsstEnd, metaAbsStart, ref layout);
        }
    }

    private static bool ParseMetadata(
        ReadOnlySpan<byte> metaBuf, long hsstStart, long hsstEnd, long metaAbsStart, ref Layout layout)
    {
        int p = 0;
        int keySize = Leb128.Read(metaBuf, ref p);
        int entryCount = Leb128.Read(metaBuf, ref p);
        int tableSize = Leb128.Read(metaBuf, ref p);
        int entriesPerCk0Log2 = Leb128.Read(metaBuf, ref p);
        int recordsPerCkHigherLog2 = Leb128.Read(metaBuf, ref p);
        int entriesByteLen = Leb128.Read(metaBuf, ref p);
        int depth = Leb128.Read(metaBuf, ref p);
        if (keySize < 0 || entryCount < 0 || tableSize < 0 ||
            entriesPerCk0Log2 < 0 || recordsPerCkHigherLog2 < 0 ||
            entriesByteLen < 0 || depth < 0) return false;
        if (keySize > 255) return false;
        if (depth > HsstPackedArrayLayout.MaxSummaryDepth) return false;
        if (entriesPerCk0Log2 > 30 || recordsPerCkHigherLog2 > 30) return false;
        if (depth >= 2 && recordsPerCkHigherLog2 < 1) return false;

        layout.HsstStart = hsstStart;
        layout.HsstEnd = hsstEnd;
        layout.KeySize = keySize;
        layout.EntryCount = entryCount;
        layout.EntriesByteLen = entriesByteLen;
        layout.HashTableSize = tableSize;
        layout.Depth = depth;
        layout.EntriesPerCkLevel0Log2 = entriesPerCk0Log2;
        layout.RecordsPerCkHigherLog2 = recordsPerCkHigherLog2;

        Span<int> counts = stackalloc int[HsstPackedArrayLayout.MaxSummaryDepth];
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
            layout.LevelStarts[lvl] = (int)(lvlStart - hsstStart);
            cursor = lvlStart;
        }

        // EntryMetaStarts: EntryCount × 4 bytes immediately before summaries.
        long entryMetaStartsBytes = (long)entryCount * 4;
        long entryMetaStartsStart = cursor - entryMetaStartsBytes;
        if (entryMetaStartsStart < hsstStart) return false;
        layout.EntryMetaStartsStart = entryMetaStartsStart;

        // Entries section starts at hsstStart and has length EntriesByteLen.
        if (hsstStart + entriesByteLen != entryMetaStartsStart) return false;

        return true;
    }

    /// <summary>
    /// Exact-match or floor lookup over a PackedArrayVariableValue HSST. On success
    /// sets <paramref name="resultBound"/> to the value region of the matched entry.
    /// </summary>
    public static bool TrySeek<TReader, TPin>(
        scoped in TReader reader, Bound bound, scoped ReadOnlySpan<byte> key,
        bool exactMatch, out Bound resultBound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        resultBound = default;
        if (!TryReadLayout<TReader, TPin>(in reader, bound, out Layout L)) return false;
        if (L.EntryCount == 0) return false;

        // Combined header+key buffer: LEB128 (≤5) + KeyLength (1) + Key (≤255).
        Span<byte> hdrBuf = stackalloc byte[6 + 255];
        Span<byte> keyCmp = stackalloc byte[255];
        Span<byte> keyCmpSlice = keyCmp[..L.KeySize];

        // Hash fast path: only for keys of the right length when a table is present.
        if (key.Length == L.KeySize && L.HashTableSize > 0)
        {
            uint h = HsstHash.HashKey(key);
            uint slot = HsstHash.Slot(h, L.HashTableSize);
            Span<byte> slotBuf = stackalloc byte[4];
            if (!reader.TryRead(L.HashTableStart + slot * 4, slotBuf)) return false;
            uint slotValue = BinaryPrimitives.ReadUInt32LittleEndian(slotBuf);

            const uint Empty = 0u;
            const uint Collision = 0xFFFFFFFFu;

            // Empty (0) is ambiguous in the BTreeHashIndex-compatible slot encoding:
            // a real entry with MetadataStart == 0 (first entry, zero-length value)
            // collides with the "empty slot" sentinel. Fall through to summary descent
            // in that case rather than declaring a miss.
            if (slotValue != Empty && slotValue != Collision)
            {
                long metaAbs = L.HsstStart + slotValue;
                if (!TryReadHeaderAndKey<TReader, TPin>(in reader, metaAbs, L.HsstEnd, L.KeySize,
                        hdrBuf, out int valueLen, out long valueAbsStart, out int keyOffsetInHdr))
                    return false;
                ReadOnlySpan<byte> entryKey = hdrBuf.Slice(keyOffsetInHdr, L.KeySize);
                if (entryKey.SequenceEqual(key))
                {
                    resultBound = new Bound(valueAbsStart, valueLen);
                    return true;
                }
                if (exactMatch) return false;
            }
        }

        // Recursive summary descent (identical to PackedArray; key fetch is via
        // EntryMetaStarts indirection, but slab geometry only depends on indices).
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
                    in reader, L.LevelAbsStart(curLvl), L.KeySize, levelLo, levelHi + 1, key, out bool readOk);
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

        // Binary search [rangeStart, rangeEnd] for smallest entry whose key ≥ target.
        int lo = rangeStart;
        int hi = rangeEnd + 1;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (!TryReadEntryKey<TReader, TPin>(in reader, in L, mid, hdrBuf, keyCmpSlice))
                return false;
            if (keyCmpSlice.SequenceCompareTo(key) < 0) lo = mid + 1;
            else hi = mid;
        }

        if (lo <= rangeEnd)
        {
            if (!TryReadEntryFull<TReader, TPin>(in reader, in L, lo, hdrBuf,
                    out int valueLenAtLo, out long valueAbsStartAtLo, out int keyOffsetAtLo))
                return false;
            ReadOnlySpan<byte> entryKey = hdrBuf.Slice(keyOffsetAtLo, L.KeySize);
            if (entryKey.SequenceEqual(key))
            {
                resultBound = new Bound(valueAbsStartAtLo, valueLenAtLo);
                return true;
            }
        }
        if (exactMatch) return false;

        // Floor: take the previous entry.
        int floorIdx = lo - 1;
        if (floorIdx < 0) return false;
        if (!TryReadEntryFull<TReader, TPin>(in reader, in L, floorIdx, hdrBuf,
                out int valueLenFloor, out long valueAbsStartFloor, out _))
            return false;
        resultBound = new Bound(valueAbsStartFloor, valueLenFloor);
        return true;
    }

    /// <summary>
    /// Fetch entry <paramref name="entryIdx"/>'s key into <paramref name="keyDst"/>.
    /// Performs the EntryMetaStarts u32 read followed by a single header+key read.
    /// </summary>
    private static bool TryReadEntryKey<TReader, TPin>(
        scoped in TReader reader, scoped in Layout L, int entryIdx,
        Span<byte> hdrBuf, Span<byte> keyDst)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        Span<byte> metaBuf = stackalloc byte[4];
        if (!reader.TryRead(L.EntryMetaStartAbs(entryIdx), metaBuf)) return false;
        uint metaStart32 = BinaryPrimitives.ReadUInt32LittleEndian(metaBuf);
        long metaAbs = L.HsstStart + metaStart32;
        if (!TryReadHeaderAndKey<TReader, TPin>(in reader, metaAbs, L.HsstEnd, L.KeySize,
                hdrBuf, out _, out _, out int keyOffsetInHdr))
            return false;
        hdrBuf.Slice(keyOffsetInHdr, L.KeySize).CopyTo(keyDst);
        return true;
    }

    /// <summary>
    /// Like <see cref="TryReadEntryKey"/> but also returns value bound info so callers
    /// can resolve the matched entry's value region. <paramref name="hdrBuf"/> retains
    /// the header+key bytes for caller-side key compare via <paramref name="keyOffsetInHdr"/>.
    /// </summary>
    private static bool TryReadEntryFull<TReader, TPin>(
        scoped in TReader reader, scoped in Layout L, int entryIdx,
        Span<byte> hdrBuf, out int valueLen, out long valueAbsStart, out int keyOffsetInHdr)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        valueLen = 0; valueAbsStart = 0; keyOffsetInHdr = 0;
        Span<byte> metaBuf = stackalloc byte[4];
        if (!reader.TryRead(L.EntryMetaStartAbs(entryIdx), metaBuf)) return false;
        uint metaStart32 = BinaryPrimitives.ReadUInt32LittleEndian(metaBuf);
        long metaAbs = L.HsstStart + metaStart32;
        return TryReadHeaderAndKey<TReader, TPin>(in reader, metaAbs, L.HsstEnd, L.KeySize,
            hdrBuf, out valueLen, out valueAbsStart, out keyOffsetInHdr);
    }

    /// <summary>
    /// Read the BTree-format entry header at <paramref name="metaAbs"/>:
    /// <c>[ValueLength: LEB128][KeyLength: u8][FullKey]</c>. Fills
    /// <paramref name="hdrBuf"/> with the (LEB128 + KeyLength + Key) byte sequence and
    /// returns the value-region bounds and the offset of the key inside hdrBuf.
    /// </summary>
    private static bool TryReadHeaderAndKey<TReader, TPin>(
        scoped in TReader reader, long metaAbs, long hsstEnd, int keySize,
        Span<byte> hdrBuf, out int valueLen, out long valueAbsStart, out int keyOffsetInHdr)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        valueLen = 0; valueAbsStart = 0; keyOffsetInHdr = 0;
        if (metaAbs < 0 || metaAbs >= hsstEnd) return false;

        int needed = 6 + keySize;
        long remaining = hsstEnd - metaAbs;
        int avail = (int)Math.Min(needed, remaining);
        if (avail < 2) return false;

        Span<byte> hdr = hdrBuf[..avail];
        if (!reader.TryRead(metaAbs, hdr)) return false;

        int pos = 0;
        int v = Leb128.Read(hdr, ref pos);
        if (v < 0 || pos >= avail) return false;
        int keyLenByte = hdr[pos++];
        if (keyLenByte != keySize) return false;
        if (pos + keySize > avail) return false;

        valueLen = v;
        valueAbsStart = metaAbs - v;
        keyOffsetInHdr = pos;
        return true;
    }

    /// <summary>
    /// Binary-search a summary level slab <c>[lo, hi)</c> for the smallest checkpoint
    /// whose key is &gt;= <paramref name="key"/>. Each summary record is exactly
    /// <paramref name="keySize"/> bytes.
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
