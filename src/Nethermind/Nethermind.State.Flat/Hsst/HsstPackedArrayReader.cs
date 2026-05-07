// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    /// Parsed footer of a PackedArray HSST: section starts and per-level summary geometry.
    /// <see cref="LevelStarts"/> entries are <see cref="long"/> offsets relative to
    /// <see cref="DataStart"/> (= start of the HSST), so the in-memory layout imposes
    /// no per-HSST size ceiling beyond what <see cref="long"/> can address.
    ///
    /// Implied limits (non-empty HSST, i.e. Depth ≥ 1):
    /// - <see cref="EntryCount"/> ≤ <see cref="int.MaxValue"/> (LEB128-decoded into int).
    /// - <see cref="LevelCounts"/>[i] ≤ <see cref="int.MaxValue"/> per level (same).
    /// Empty (Depth = 0) HSSTs carry no summary, so depth-dependent invariants don't apply.
    ///
    /// The on-disk format does not store offsets — only LEB128 counts and sizes — so widening
    /// or narrowing this struct has no format impact.
    /// </summary>
    internal ref struct Layout
    {
        public long DataStart;
        public int KeySize;
        public int ValueSize;
        public int EntryCount;
        public int Depth;
        public int EntriesPerCkLevel0Log2;
        public int RecordsPerCkHigherLog2;
        // Inline arrays sized to MaxSummaryDepth. Only [0..Depth) are valid.
        // LevelStarts uses long offsets; LevelCounts is int because per-level counts
        // are LEB128-decoded into int (~2.1 B per level — independent of total HSST size).
        public InlineLongLevelArray LevelStarts;
        public InlineIntLevelArray LevelCounts;

        public int EntryStride => KeySize + ValueSize;
        public long EntryAbsStart(int entryIdx) => DataStart + (long)entryIdx * EntryStride;
        public long ValueAbsStart(int entryIdx) => EntryAbsStart(entryIdx) + KeySize;
        public long LevelAbsStart(int level) => DataStart + LevelStarts[level];
    }

    [System.Runtime.CompilerServices.InlineArray(HsstPackedArrayLayout.MaxSummaryDepth)]
    internal struct InlineLongLevelArray
    {
        private long _e0;
    }

    [System.Runtime.CompilerServices.InlineArray(HsstPackedArrayLayout.MaxSummaryDepth)]
    internal struct InlineIntLevelArray
    {
        private int _e0;
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
        int p = 0;
        int keySize = Leb128.Read(metaBuf, ref p);
        int valueSize = Leb128.Read(metaBuf, ref p);
        int entryCount = Leb128.Read(metaBuf, ref p);
        int entriesPerCk0Log2 = Leb128.Read(metaBuf, ref p);
        int recordsPerCkHigherLog2 = Leb128.Read(metaBuf, ref p);
        int depth = Leb128.Read(metaBuf, ref p);
        if (keySize < 0 || valueSize < 0 || entryCount < 0 ||
            entriesPerCk0Log2 < 0 || recordsPerCkHigherLog2 < 0 || depth < 0) return false;
        if (keySize > 255) return false;
        if (depth > HsstPackedArrayLayout.MaxSummaryDepth) return false;
        // Clamp shifts to a safe range — bigger than 30 would overflow int slab arithmetic.
        if (entriesPerCk0Log2 > 30 || recordsPerCkHigherLog2 > 30) return false;
        if (depth >= 2 && recordsPerCkHigherLog2 < 1) return false;

        layout.KeySize = keySize;
        layout.ValueSize = valueSize;
        layout.EntryCount = entryCount;
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

        // Summaries lie immediately before the metadata. Each record is exactly KeySize bytes.
        // Stored as long offsets from hsstStart — see Layout's type doc for why this isn't
        // truncating, and for the on-disk format's lack of any persisted offset.
        long cursor = metaAbsStart;
        for (int lvl = depth - 1; lvl >= 0; lvl--)
        {
            long lvlBytes = (long)counts[lvl] * keySize;
            long lvlStart = cursor - lvlBytes;
            if (lvlStart < hsstStart) return false;
            layout.LevelStarts[lvl] = lvlStart - hsstStart;
            cursor = lvlStart;
        }

        long dataBytes = (long)entryCount * (keySize + valueSize);
        if (hsstStart + dataBytes != cursor) return false;
        layout.DataStart = hsstStart;

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

        // Binary search [rangeStart, rangeEnd] in Data for the smallest entry whose key
        // is >= target.
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
