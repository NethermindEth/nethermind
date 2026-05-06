// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Read-side helpers for the <see cref="IndexType.VarPackedArray"/> layout. Mirrors
/// <see cref="HsstPackedArrayReader"/> but the data section is split: variable-length
/// values come first, followed by a fixed-stride key+offset table that the binary
/// search and recursive summary descent operate over.
/// </summary>
internal static class HsstVarPackedArrayReader
{
    /// <summary>
    /// Parsed footer of a VarPackedArray HSST. Section starts and per-level summary
    /// geometry. <see cref="LevelStarts"/> entries are int offsets relative to
    /// <see cref="HsstStart"/>; the HSST is capped at ≈2 GiB so 32-bit offsets suffice.
    /// </summary>
    internal ref struct Layout
    {
        public long HsstStart;
        public long ValuesStart;
        public long KeyOffsetsStart;
        public long ValuesTotalLength;
        public int KeySize;
        public int OffsetSize;
        public int EntryCount;
        public int Depth;
        public int EntriesPerCkLevel0Log2;
        public int RecordsPerCkHigherLog2;
        public HsstPackedArrayReader.InlineLevelArray LevelStarts;
        public HsstPackedArrayReader.InlineLevelArray LevelCounts;

        public int EntryStride => KeySize + OffsetSize;
        public long EntryAbsStart(int entryIdx) => KeyOffsetsStart + (long)entryIdx * EntryStride;
        public long EndOffsetAbsStart(int entryIdx) => EntryAbsStart(entryIdx) + KeySize;
        public long LevelAbsStart(int level) => HsstStart + (uint)LevelStarts[level];
    }

    /// <summary>
    /// Tail window pinned by <see cref="TryReadLayout"/>. Sized to fit every VarPackedArray
    /// metadata block emitted by the current builder (well under 64 B in practice).
    /// </summary>
    private const int TailWindowSize = 64;

    /// <summary>
    /// Parse the VarPackedArray footer. Returns false on truncation or self-inconsistency.
    /// </summary>
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
                return ParseMetadata(metaSpan, hsstStart, metaAbsStart, ref layout);
            }
        }

        using (TPin metaPin = reader.PinBuffer(metaAbsStart, metaLen))
        {
            return ParseMetadata(metaPin.Buffer, hsstStart, metaAbsStart, ref layout);
        }
    }

    private static bool ParseMetadata(
        ReadOnlySpan<byte> metaBuf, long hsstStart, long metaAbsStart, ref Layout layout)
    {
        int p = 0;
        int keySize = Leb128.Read(metaBuf, ref p);
        int offsetSize = Leb128.Read(metaBuf, ref p);
        int entryCount = Leb128.Read(metaBuf, ref p);
        long valuesTotal = ReadLeb128Long(metaBuf, ref p);
        int entriesPerCk0Log2 = Leb128.Read(metaBuf, ref p);
        int recordsPerCkHigherLog2 = Leb128.Read(metaBuf, ref p);
        int depth = Leb128.Read(metaBuf, ref p);
        if (keySize < 0 || entryCount < 0 || valuesTotal < 0 ||
            entriesPerCk0Log2 < 0 || recordsPerCkHigherLog2 < 0 || depth < 0) return false;
        if (keySize > 255) return false;
        if (offsetSize is not (1 or 2 or 4 or 6)) return false;
        if (depth > HsstPackedArrayLayout.MaxSummaryDepth) return false;
        if (entriesPerCk0Log2 > 30 || recordsPerCkHigherLog2 > 30) return false;
        if (depth >= 2 && recordsPerCkHigherLog2 < 1) return false;

        layout.KeySize = keySize;
        layout.OffsetSize = offsetSize;
        layout.EntryCount = entryCount;
        layout.ValuesTotalLength = valuesTotal;
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

        // Summaries lie immediately before the metadata.
        long cursor = metaAbsStart;
        for (int lvl = depth - 1; lvl >= 0; lvl--)
        {
            long lvlBytes = (long)counts[lvl] * keySize;
            long lvlStart = cursor - lvlBytes;
            if (lvlStart < hsstStart) return false;
            layout.LevelStarts[lvl] = (int)(lvlStart - hsstStart);
            cursor = lvlStart;
        }

        // KeyOffsets section ends where the lowest summary starts.
        long keyOffsetsBytes = (long)entryCount * (keySize + offsetSize);
        long keyOffsetsStart = cursor - keyOffsetsBytes;
        if (keyOffsetsStart < hsstStart) return false;

        long valuesStart = keyOffsetsStart - valuesTotal;
        if (valuesStart != hsstStart) return false;

        layout.HsstStart = hsstStart;
        layout.ValuesStart = valuesStart;
        layout.KeyOffsetsStart = keyOffsetsStart;
        return true;
    }

    /// <summary>
    /// Exact-match or floor lookup over a VarPackedArray HSST. On success sets
    /// <paramref name="resultBound"/> to the value region of the matched entry
    /// inside the Values section.
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

        Span<byte> keyCmp = stackalloc byte[255];
        Span<byte> keyCmpSlice = keyCmp[..L.KeySize];

        // Recursive summary descent — identical to PackedArray.
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

        // Binary search [rangeStart, rangeEnd] on the key+offset table.
        int lo = rangeStart;
        int hi = rangeEnd + 1;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (!reader.TryRead(L.EntryAbsStart(mid), keyCmpSlice)) return false;
            if (keyCmpSlice.SequenceCompareTo(key) < 0) lo = mid + 1;
            else hi = mid;
        }
        if (lo <= rangeEnd)
        {
            if (!reader.TryRead(L.EntryAbsStart(lo), keyCmpSlice)) return false;
            if (keyCmpSlice.SequenceEqual(key))
            {
                return TryGetValueBound<TReader, TPin>(in reader, in L, lo, out resultBound);
            }
        }
        if (exactMatch) return false;

        int floorIdx = lo - 1;
        if (floorIdx < 0) return false;
        return TryGetValueBound<TReader, TPin>(in reader, in L, floorIdx, out resultBound);
    }

    /// <summary>
    /// Resolve entry <paramref name="entryIdx"/>'s value region by reading its end offset
    /// (and, for non-zero indices, the previous end offset) from the key+offset table.
    /// </summary>
    private static bool TryGetValueBound<TReader, TPin>(
        scoped in TReader reader, scoped in Layout L, int entryIdx, out Bound bound)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        bound = default;
        Span<byte> buf = stackalloc byte[8];
        long start;
        if (entryIdx == 0)
        {
            start = 0;
        }
        else
        {
            buf.Clear();
            if (!reader.TryRead(L.EndOffsetAbsStart(entryIdx - 1), buf[..L.OffsetSize])) return false;
            start = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf);
        }
        buf.Clear();
        if (!reader.TryRead(L.EndOffsetAbsStart(entryIdx), buf[..L.OffsetSize])) return false;
        long end = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf);
        if (end < start || end > L.ValuesTotalLength) return false;
        bound = new Bound(L.ValuesStart + start, end - start);
        return true;
    }

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

    /// <summary>Long-valued LEB128 reader paired with the builder's WriteLeb128Long.</summary>
    private static long ReadLeb128Long(ReadOnlySpan<byte> data, ref int offset)
    {
        long result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = data[offset++];
            result |= (long)(b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);
        return result;
    }
}
