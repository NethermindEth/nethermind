// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History.Segmented;

/// <summary>
/// Approach-2 change index for one flat domain (account, storage, or storage-clears) as a set of immutable, memory-mapped
/// Elias-Fano <see cref="HistorySegment"/> files. Recorded changes accumulate in memory for the current step and are
/// sealed into a new segment at a block/size boundary (or on <see cref="Flush"/>); a read consults the in-memory
/// buffer then the segments newest-first, so the first segment that holds a change at/before the query block wins.
/// Segments never overlap in block range and are periodically merged to bound their count.
/// </summary>
/// <remarks>
/// No RocksDB: the segment directory is the durable store, and each file self-describes its block range in its
/// header, so the set of files on disk IS the manifest — the constructor rebuilds state by scanning them.
/// Re-delivery of a block at or below the last sealed block is dropped, keeping a rescanning writer idempotent.
/// </remarks>
public sealed class SegmentHistoryStore : IDisposable
{
    private const string SegmentExtension = ".hs";

    private readonly string _directory;
    private readonly int _keyLen;
    private readonly int _stepBlocks;
    private readonly long _maxBufferBytes;
    private readonly int _mergeFanout;
    private readonly long _maxSegmentBlocks;

    // Segments ordered by ascending FromBlock (newest last); ranges are disjoint.
    private readonly List<HistorySegment> _segments = [];

    // Current unsealed step, keyed by flat key (sorted for sealing). Per entity: parallel block/value lists in
    // ascending block order.
    private readonly SortedDictionary<byte[], EntityBuffer> _buffer = new(ByteArrayComparer.Instance);
    private long _bufferBytes;
    private ulong _bufferFirstBlock;
    private bool _bufferStarted;

    private ulong _durableMaxBlock;
    private bool _anyDurable;
    private ulong _firstCompletedBlock;
    private ulong _lastCompletedBlock;
    private bool _anyCompleted;

    public SegmentHistoryStore(
        string directory,
        int keyLen,
        int stepBlocks = 4096,
        long maxBufferBytes = 256L * 1024 * 1024,
        int mergeFanout = 8,
        long maxSegmentBlocks = 2_097_152)
    {
        _directory = directory;
        _keyLen = keyLen;
        _stepBlocks = stepBlocks;
        _maxBufferBytes = maxBufferBytes;
        _mergeFanout = Math.Max(2, mergeFanout); // fanout of 1 would merge a single segment into itself forever
        _maxSegmentBlocks = maxSegmentBlocks;

        Directory.CreateDirectory(directory);
        foreach (string path in Directory.EnumerateFiles(directory, "seg_*" + SegmentExtension))
            _segments.Add(new HistorySegment(path));
        _segments.Sort(static (a, b) => a.FromBlock.CompareTo(b.FromBlock));

        if (_segments.Count > 0)
        {
            _anyDurable = _anyCompleted = true;
            _firstCompletedBlock = _segments[0].FromBlock;
            _durableMaxBlock = _lastCompletedBlock = _segments[^1].ToBlock;
        }
    }

    public void RecordChange(ulong block, scoped ReadOnlySpan<byte> flatKey, scoped ReadOnlySpan<byte> value)
    {
        if (_anyDurable && block <= _durableMaxBlock) return; // already sealed; keep resume idempotent

        if (!_buffer.TryGetValue(GetKeyForLookup(flatKey), out EntityBuffer? entity))
        {
            entity = new EntityBuffer();
            _buffer.Add(flatKey.ToArray(), entity);
        }

        entity.Blocks.Add(block);
        entity.Values.Add(value.IsEmpty ? [] : value.ToArray());
        _bufferBytes += value.Length + sizeof(ulong) + sizeof(uint);
        StartStepIfNeeded(block);
    }

    public void CompleteBlock(ulong block)
    {
        if (_anyDurable && block <= _durableMaxBlock) return;

        if (!_anyCompleted)
        {
            _firstCompletedBlock = block;
            _anyCompleted = true;
        }
        _lastCompletedBlock = block;
        StartStepIfNeeded(block);

        bool stepFull = block - _bufferFirstBlock + 1 >= (ulong)_stepBlocks;
        if (stepFull || _bufferBytes >= _maxBufferBytes) Seal(block);
    }

    public void Flush()
    {
        if (_buffer.Count > 0) Seal(_lastCompletedBlock);
    }

    public int TryGetAt(ulong block, scoped ReadOnlySpan<byte> flatKey, Span<byte> outBuffer, out ulong foundAtBlock)
    {
        // Buffer (current step) is newest; then segments newest-first. First hit is the latest change at/before block.
        int buffered = TryGetFromBuffer(block, flatKey, outBuffer, out foundAtBlock);
        if (buffered != -1) return buffered;

        for (int s = _segments.Count - 1; s >= 0; s--)
        {
            HistorySegment segment = _segments[s];
            if (segment.FromBlock > block) continue;
            int written = segment.TryGetAt(flatKey, block, outBuffer, out foundAtBlock);
            if (written != -1) return written;
        }

        foundAtBlock = 0;
        return -1;
    }

    public bool HasChangeInRange(scoped ReadOnlySpan<byte> flatKey, ulong afterExclusive, ulong atOrBefore)
    {
        if (afterExclusive >= atOrBefore) return false;

        if (_buffer.TryGetValue(GetKeyForLookup(flatKey), out EntityBuffer? entity)
            && FloorIndex(entity.Blocks, atOrBefore) is int idx and >= 0
            && entity.Blocks[idx] > afterExclusive)
        {
            return true;
        }

        for (int s = _segments.Count - 1; s >= 0; s--)
        {
            HistorySegment segment = _segments[s];
            if (segment.FromBlock > atOrBefore) continue;
            if (segment.ToBlock <= afterExclusive) break; // older segments are entirely at/below the lower bound
            if (segment.HasChangeInRange(flatKey, afterExclusive, atOrBefore)) return true;
        }
        return false;
    }

    public bool CoversBlock(ulong block) =>
        _anyCompleted && block >= _firstCompletedBlock && block <= _lastCompletedBlock;

    private void StartStepIfNeeded(ulong block)
    {
        if (_bufferStarted) return;
        _bufferFirstBlock = block;
        _bufferStarted = true;
    }

    private int TryGetFromBuffer(ulong block, scoped ReadOnlySpan<byte> flatKey, Span<byte> outBuffer, out ulong foundAtBlock)
    {
        foundAtBlock = 0;
        if (!_buffer.TryGetValue(GetKeyForLookup(flatKey), out EntityBuffer? entity)) return -1;

        int idx = FloorIndex(entity.Blocks, block);
        if (idx < 0) return -1; // key present but all its changes are newer than block

        foundAtBlock = entity.Blocks[idx];
        byte[] value = entity.Values[idx];
        value.CopyTo(outBuffer);
        return value.Length;
    }

    // Index of the largest block <= query in the ascending list, or -1.
    private static int FloorIndex(List<ulong> blocks, ulong query)
    {
        int lo = 0, hi = blocks.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (blocks[mid] <= query) { result = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return result;
    }

    private void Seal(ulong toBlock)
    {
        if (_buffer.Count == 0)
        {
            _bufferStarted = false;
            _bufferBytes = 0;
            return;
        }

        ulong fromBlock = _bufferFirstBlock;
        List<HistoryChangeEntry> entries = new(_buffer.Count);
        foreach ((byte[] key, EntityBuffer entity) in _buffer)
            entries.Add(new HistoryChangeEntry(key, [.. entity.Blocks], [.. entity.Values]));

        string path = SegmentPath(fromBlock, toBlock);
        HistorySegment.Write(path, _keyLen, fromBlock, toBlock, entries);
        _segments.Add(new HistorySegment(path));

        _durableMaxBlock = toBlock;
        _anyDurable = true;
        _buffer.Clear();
        _bufferBytes = 0;
        _bufferStarted = false;

        MergeIfNeeded();
    }

    // Size-tiered merge: a segment's tier is derived from its block span (tier t spans [StepBlocks·F^t, StepBlocks·F^(t+1))).
    // Whenever `F` consecutive segments share a tier, the oldest `F` of them fuse into one segment of the next tier up.
    // A segment whose span reaches `MaxSegmentBlocks` is frozen and never merged again, so every block is rewritten at
    // most log_F(MaxSegmentBlocks/StepBlocks) times (bounded write amplification) and the file count stays logarithmic
    // in chain length plus one frozen file per MaxSegmentBlocks. Older segments are larger, so same-tier runs are always
    // contiguous and merges preserve the disjoint ascending-range invariant.
    private void MergeIfNeeded()
    {
        while (TryFindMergeableRun(out int start, out int count))
            MergeRange(start, count);
    }

    // Oldest run of exactly `_mergeFanout` consecutive same-tier, non-frozen segments, if any.
    private bool TryFindMergeableRun(out int start, out int count)
    {
        int i = 0;
        while (i < _segments.Count)
        {
            int tier = TierOf(_segments[i]);
            if (tier == FrozenTier) { i++; continue; }

            int j = i + 1;
            while (j < _segments.Count && TierOf(_segments[j]) == tier) j++;
            if (j - i >= _mergeFanout)
            {
                start = i;
                count = _mergeFanout;
                return true;
            }
            i = j;
        }

        start = count = 0;
        return false;
    }

    private const int FrozenTier = int.MaxValue;

    private int TierOf(HistorySegment segment)
    {
        long span = (long)(segment.ToBlock - segment.FromBlock + 1);
        if (span >= _maxSegmentBlocks) return FrozenTier;

        int tier = 0;
        long upper = (long)_stepBlocks * _mergeFanout;
        while (span >= upper) { upper *= _mergeFanout; tier++; }
        return tier;
    }

    private void MergeRange(int start, int count)
    {
        HistorySegment[] group = _segments.GetRange(start, count).ToArray();
        List<HistoryChangeEntry> merged = MergeEntries(group);

        ulong fromBlock = group[0].FromBlock;
        ulong toBlock = group[^1].ToBlock;
        string path = SegmentPath(fromBlock, toBlock);
        HistorySegment.Write(path, _keyLen, fromBlock, toBlock, merged);

        foreach (HistorySegment segment in group)
        {
            string segmentPath = segment.Path;
            segment.Dispose();
            if (segmentPath != path) File.Delete(segmentPath);
        }

        _segments.RemoveRange(start, count);
        _segments.Insert(start, new HistorySegment(path));
    }

    // K-way merge-join of disjoint segments (ordered by ascending block range) on their sorted key tables; a key present
    // in several segments concatenates their change lists in segment (== block) order, which stays ascending.
    private static List<HistoryChangeEntry> MergeEntries(ReadOnlySpan<HistorySegment> group)
    {
        int k = group.Length;
        int[] cursor = new int[k];
        int capacity = 0;
        for (int s = 0; s < k; s++) capacity += group[s].Count;

        List<HistoryChangeEntry> result = new(capacity);
        while (true)
        {
            int min = -1;
            for (int s = 0; s < k; s++)
            {
                if (cursor[s] >= group[s].Count) continue;
                if (min == -1 || group[s].KeyAt(cursor[s]).SequenceCompareTo(group[min].KeyAt(cursor[min])) < 0)
                    min = s;
            }
            if (min == -1) break;

            ReadOnlySpan<byte> key = group[min].KeyAt(cursor[min]);
            HistoryChangeEntry? entry = null;
            for (int s = 0; s < k; s++)
            {
                if (cursor[s] < group[s].Count && group[s].KeyAt(cursor[s]).SequenceCompareTo(key) == 0)
                {
                    HistoryChangeEntry next = group[s].ReadEntry(cursor[s]++);
                    entry = entry is null ? next : Concat(entry, next);
                }
            }
            result.Add(entry!);
        }
        return result;
    }

    private static HistoryChangeEntry Concat(HistoryChangeEntry older, HistoryChangeEntry newer)
    {
        ulong[] blocks = [.. older.Blocks, .. newer.Blocks];
        byte[][] values = [.. older.Values, .. newer.Values];
        return new HistoryChangeEntry(older.Key, blocks, values);
    }

    private string SegmentPath(ulong fromBlock, ulong toBlock) =>
        System.IO.Path.Combine(_directory, $"seg_{fromBlock:D18}_{toBlock:D18}{SegmentExtension}");

    private byte[] GetKeyForLookup(scoped ReadOnlySpan<byte> flatKey)
    {
        // SortedDictionary lookups need a byte[]; reuse a scratch of the fixed key length to avoid allocating on reads.
        byte[] scratch = _lookupKey ??= new byte[_keyLen];
        flatKey.CopyTo(scratch);
        return scratch;
    }

    private byte[]? _lookupKey;

    public void Dispose()
    {
        Flush(); // seal the last partial step so it survives a restart
        foreach (HistorySegment segment in _segments) segment.Dispose();
        _segments.Clear();
    }

    private sealed class EntityBuffer
    {
        public List<ulong> Blocks { get; } = [];
        public List<byte[]> Values { get; } = [];
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
