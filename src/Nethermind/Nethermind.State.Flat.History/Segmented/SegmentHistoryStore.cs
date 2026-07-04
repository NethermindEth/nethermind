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
    private readonly int _maxSegmentsBeforeMerge;

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
        int maxSegmentsBeforeMerge = 16)
    {
        _directory = directory;
        _keyLen = keyLen;
        _stepBlocks = stepBlocks;
        _maxBufferBytes = maxBufferBytes;
        _maxSegmentsBeforeMerge = maxSegmentsBeforeMerge;

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

        MergeOldestIfNeeded();
    }

    // TODO!: merging the oldest pair makes segment[0] grow to ~full-archive size and be rewritten on every seal (O(N^2) write amplification) — replace with a tiered/leveled merge.
    private void MergeOldestIfNeeded()
    {
        while (_segments.Count > _maxSegmentsBeforeMerge)
        {
            HistorySegment older = _segments[0];
            HistorySegment newer = _segments[1];
            List<HistoryChangeEntry> merged = MergeEntries(older, newer);

            string path = SegmentPath(older.FromBlock, newer.ToBlock);
            HistorySegment.Write(path, _keyLen, older.FromBlock, newer.ToBlock, merged);

            string olderPath = older.Path, newerPath = newer.Path;
            older.Dispose();
            newer.Dispose();
            if (olderPath != path) File.Delete(olderPath);
            if (newerPath != path) File.Delete(newerPath);

            _segments.RemoveRange(0, 2);
            _segments.Insert(0, new HistorySegment(path));
        }
    }

    // Merge-join two disjoint segments (older.ToBlock < newer.FromBlock) by key; shared keys concatenate their
    // already-ordered change lists.
    private static List<HistoryChangeEntry> MergeEntries(HistorySegment older, HistorySegment newer)
    {
        List<HistoryChangeEntry> result = new(older.Count + newer.Count);
        int i = 0, j = 0;
        while (i < older.Count && j < newer.Count)
        {
            int cmp = older.KeyAt(i).SequenceCompareTo(newer.KeyAt(j));
            if (cmp < 0) result.Add(older.ReadEntry(i++));
            else if (cmp > 0) result.Add(newer.ReadEntry(j++));
            else result.Add(Concat(older.ReadEntry(i++), newer.ReadEntry(j++)));
        }
        while (i < older.Count) result.Add(older.ReadEntry(i++));
        while (j < newer.Count) result.Add(newer.ReadEntry(j++));
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
