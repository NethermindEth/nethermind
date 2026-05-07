// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Read-side helpers for the <see cref="IndexType.PackedArray"/> layout. Stateless static
/// methods so <see cref="HsstReader{TReader,TPin}"/> can dispatch into them without copying
/// its ref-struct state.
/// </summary>
internal static class HsstPackedArrayReader
{
    /// <summary>
    /// Parsed footer of a PackedArray HSST: scalar geometry only. Per-level record counts
    /// and absolute level start offsets are NOT stored on Layout — the descent recomputes
    /// them via <see cref="ComputeLevelGeometry"/> (≤ <see cref="HsstPackedArrayLayout.MaxSummaryDepth"/>
    /// integer ops).
    ///
    /// On disk, <see cref="EntryCount"/> is a fixed <c>u32 LE</c> (the builder caps
    /// entry count at <see cref="int.MaxValue"/> — its checkpoint staging buffers are
    /// byte-indexed by <see cref="int"/>); other fields are <c>u8</c>.
    /// </summary>
    internal ref struct Layout
    {
        public long DataStart;
        /// <summary>End of the summary section / start of the metadata block. The descent
        /// uses this as its starting cursor and walks backward through the levels.</summary>
        public long SummaryEnd;
        public int KeySize;
        public int ValueSize;
        public long EntryCount;
        public int Depth;
        public int EntriesPerCkLevel0Log2;
        public int RecordsPerCkHigherLog2;

        public int EntryStride => KeySize + ValueSize;
        public long EntryAbsStart(long entryIdx) => DataStart + entryIdx * EntryStride;
        public long ValueAbsStart(long entryIdx) => EntryAbsStart(entryIdx) + KeySize;
    }

    /// <summary>
    /// Reconstruct per-level record counts from the scalar Layout. Mirrors the builder:
    ///   counts[0]   = ceil(EntryCount / (1 &lt;&lt; EntriesPerCkLevel0Log2))
    ///   counts[k+1] = ceil(counts[k]   / (1 &lt;&lt; RecordsPerCkHigherLog2))
    /// Writes <c>L.Depth</c> entries into <paramref name="counts"/>. Returns false if the
    /// recurrence produces a non-decreasing or non-positive count (corrupt header).
    /// </summary>
    private static bool ComputeLevelCounts(in Layout L, Span<long> counts)
    {
        if (L.Depth == 0) return true;
        long n0 = 1L << L.EntriesPerCkLevel0Log2;
        long c = (L.EntryCount + n0 - 1) / n0;
        if (c <= 0) return false;
        counts[0] = c;
        long m = 1L << L.RecordsPerCkHigherLog2;
        for (int i = 1; i < L.Depth; i++)
        {
            long prev = counts[i - 1];
            long next = (prev + m - 1) / m;
            if (next <= 0 || next >= prev) return false;
            counts[i] = next;
        }
        return true;
    }

    /// <summary>
    /// Parse the PackedArray footer. Returns false on truncation or self-inconsistency.
    /// Issues a single small tail-window pin in the common case (metadata fits in
    /// <see cref="TailWindowSize"/>); only falls back to a separate read when the
    /// metadata is unusually large.
    /// </summary>
    public static bool TryReadLayout<TReader, TPin>(scoped in TReader reader, Bound bound, out Layout layout)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        layout = default;
        long hsstStart = bound.Offset;
        long hsstEnd = bound.Offset + bound.Length;

        if (bound.Length < 3) return false;

        // Tail window covers the trailing IndexType byte, MetadataLength byte, and (almost
        // always) the entire LEB128 metadata block. Real metadata is ~13–25 B; 64 B fits
        // virtually every PackedArray emitted by the builder.
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
                // Hot path: metadata fits in the same pinned window.
                ReadOnlySpan<byte> metaSpan = tail.Slice(tailLen - 2 - metaLen, metaLen);
                return ParseMetadata(metaSpan, hsstStart, metaAbsStart, ref layout);
            }
        }

        // Cold path: metadata exceeds the tail window. Re-pin precisely.
        using (TPin metaPin = reader.PinBuffer(metaAbsStart, metaLen))
        {
            return ParseMetadata(metaPin.Buffer, hsstStart, metaAbsStart, ref layout);
        }
    }

    /// <summary>
    /// Tail window pinned by <see cref="TryReadLayout"/>. Sized to fit every
    /// PackedArray metadata block emitted by the current builder (well under 64 B in
    /// practice) so the common case completes with a single pin.
    /// </summary>
    private const int TailWindowSize = 64;

    private static bool ParseMetadata(
        ReadOnlySpan<byte> metaBuf, long hsstStart, long metaAbsStart, ref Layout layout)
    {
        // Fixed 9-byte metadata: KeySize (u8), ValueSize (u8), EntryCount (u32 LE),
        // EntriesPerCkLevel0Log2 (u8), RecordsPerCkHigherLog2 (u8), Depth (u8).
        // Per-level counts are not stored — they're recomputed below from the strides.
        if (metaBuf.Length < 9) return false;
        int keySize = metaBuf[0];
        int valueSize = metaBuf[1];
        uint entryCountU32 = BinaryPrimitives.ReadUInt32LittleEndian(metaBuf[2..]);
        if (entryCountU32 > int.MaxValue) return false;
        long entryCount = entryCountU32;
        int entriesPerCk0Log2 = metaBuf[6];
        int recordsPerCkHigherLog2 = metaBuf[7];
        int depth = metaBuf[8];
        if (depth > HsstPackedArrayLayout.MaxSummaryDepth) return false;
        // Clamp shifts to a safe range — bigger than 30 would overflow int slab arithmetic.
        if (entriesPerCk0Log2 > 30 || recordsPerCkHigherLog2 > 30) return false;
        if (depth >= 2 && recordsPerCkHigherLog2 < 1) return false;

        layout.DataStart = hsstStart;
        layout.SummaryEnd = metaAbsStart;
        layout.KeySize = keySize;
        layout.ValueSize = valueSize;
        layout.EntryCount = entryCount;
        layout.Depth = depth;
        layout.EntriesPerCkLevel0Log2 = entriesPerCk0Log2;
        layout.RecordsPerCkHigherLog2 = recordsPerCkHigherLog2;

#if DEBUG
        // Self-consistency: scalar metadata must reproduce the bound's footprint exactly.
        // Skipped in release — corrupt bounds surface naturally during TrySeek's reads.
        Span<long> counts = stackalloc long[HsstPackedArrayLayout.MaxSummaryDepth];
        if (!ComputeLevelCounts(in layout, counts)) return false;
        long expectedSummaryEnd = layout.DataStart + entryCount * layout.EntryStride;
        for (int i = 0; i < depth; i++) expectedSummaryEnd += counts[i] * keySize;
        if (expectedSummaryEnd != layout.SummaryEnd) return false;
#endif

        return true;
    }

    /// <summary>
    /// Exact-match or floor lookup over a PackedArray HSST. On success sets
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

        Span<byte> keyCmp = stackalloc byte[255];
        Span<byte> keyCmpSlice = keyCmp[..L.KeySize];

        // Recursive summary descent. At each level k, the active slab is [levelLo, levelHi]
        // (closed). Find the smallest ck c with key >= target in that slab; if none, take
        // c = levelHi for floor (covers the last child slab). Slab semantics:
        //   stride = (k == 0) ? EntriesPerCkLevel0 : RecordsPerCkHigher
        //   parentCount = (k == 0) ? EntryCount : Count_{k-1}
        //   childSlab = [c*stride, min((c+1)*stride - 1, parentCount - 1)]
        long rangeStart;
        long rangeEnd;

        if (L.Depth == 0)
        {
            rangeStart = 0;
            rangeEnd = L.EntryCount - 1;
        }
        else
        {
            // Recompute per-level counts on the fly. Level start offsets aren't stored —
            // a rolling cursor walks backward through the summary section, starting at its
            // end (level Depth-1 is adjacent to the metadata block, level 0 sits right
            // after Data). Depth ≤ MaxSummaryDepth (8), so this is a handful of integer ops.
            Span<long> counts = stackalloc long[HsstPackedArrayLayout.MaxSummaryDepth];
            if (!ComputeLevelCounts(in L, counts)) return false;

            long cursor = L.SummaryEnd;

            long levelLo = 0;
            long levelHi = counts[L.Depth - 1] - 1;
            int curLvl = L.Depth - 1;
            rangeStart = 0;
            rangeEnd = -1;
            while (true)
            {
                cursor -= counts[curLvl] * L.KeySize;
                long ckIdx = SearchSummaryLevel<TReader, TPin>(
                    in reader, cursor, L.KeySize, levelLo, levelHi + 1, key, out bool readOk);
                if (!readOk) return false;

                if (ckIdx > levelHi)
                {
                    if (exactMatch) return false;
                    ckIdx = levelHi;
                }

                int strideLog2 = (curLvl == 0) ? L.EntriesPerCkLevel0Log2 : L.RecordsPerCkHigherLog2;
                long parentCount = (curLvl == 0) ? L.EntryCount : counts[curLvl - 1];
                long newLo = ckIdx << strideLog2;
                long newHi = Math.Min(((ckIdx + 1) << strideLog2) - 1, parentCount - 1);

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
        long lo = rangeStart;
        long hi = rangeEnd + 1;
        while (lo < hi)
        {
            long mid = (long)(((ulong)lo + (ulong)hi) >> 1);
            if (!reader.TryRead(L.EntryAbsStart(mid), keyCmpSlice)) return false;
            if (keyCmpSlice.SequenceCompareTo(key) < 0) lo = mid + 1;
            else hi = mid;
        }
        if (lo <= rangeEnd)
        {
            if (!reader.TryRead(L.EntryAbsStart(lo), keyCmpSlice)) return false;
            if (keyCmpSlice.SequenceEqual(key))
            {
                resultBound = new Bound(L.ValueAbsStart(lo), L.ValueSize);
                return true;
            }
        }
        if (exactMatch) return false;

        // Floor: take the previous entry (in absolute index space). Range boundaries don't
        // matter — the entry array is globally sorted.
        long floorIdx = lo - 1;
        if (floorIdx < 0) return false;
        resultBound = new Bound(L.ValueAbsStart(floorIdx), L.ValueSize);
        return true;
    }

    /// <summary>
    /// Binary-search a summary level slab `[lo, hi)` for the smallest checkpoint whose key
    /// is &gt;= <paramref name="key"/>. Returns <c>hi</c> when no such checkpoint exists.
    /// Each summary record is exactly <paramref name="keySize"/> bytes (no trailing index).
    /// </summary>
    private static long SearchSummaryLevel<TReader, TPin>(
        scoped in TReader reader, long levelStart, int keySize,
        long lo, long hi, scoped ReadOnlySpan<byte> key, out bool readOk)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        readOk = true;

        Span<byte> ckBuf = stackalloc byte[255];
        Span<byte> ckSlice = ckBuf[..keySize];
        while (lo < hi)
        {
            long mid = (long)(((ulong)lo + (ulong)hi) >> 1);
            long ckEntryStart = levelStart + mid * keySize;
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
