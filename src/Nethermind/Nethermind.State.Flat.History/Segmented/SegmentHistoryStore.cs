// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;

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
/// Crash safety: segments are published by atomic rename after fsync (see <see cref="HistorySegment.Write"/>), and
/// the constructor discards leftover scratch files and reconciles the covering/contained segments a crash mid-merge
/// can leave behind (see <see cref="ReconcileOverlaps"/>), so a crash during seal or merge self-heals on reopen.
/// Concurrency: one writer thread (block processing) may run concurrently with many reader threads (RPC). Readers are
/// lock-free over segment data — the segment set is an immutable snapshot (see <see cref="Generation"/>) captured under
/// a short lock that guards only in-memory bookkeeping (buffer, snapshot pointer, refcount), never the mmap reads. A
/// merged-away segment is unmapped and deleted only when the last reader holding the snapshot it belonged to releases
/// it, so a reader never dereferences an unmapped file.
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

    // Guards the mutable in-memory bookkeeping shared with readers: _buffer, the _generation pointer + its refcount, and
    // the completion markers. Held only for O(1)-ish in-memory work — never across a segment mmap read or file I/O.
    private readonly Lock _lock = new();

    // The live segment set, ordered by ascending FromBlock (newest last); ranges are disjoint. Published as an immutable
    // snapshot so readers iterate a stable array; only the writer replaces it, under _lock.
    private Generation _generation = new(ImmutableArray<HistorySegment>.Empty);

    // Current unsealed step, keyed by flat key. Per entity: parallel block/value lists in ascending block order.
    // A Dictionary (probed span-first via _bufferLookup) rather than a sorted map: sealing sorts once, which is cheaper
    // than maintaining tree order on every append and needs no per-read key allocation.
    private readonly Dictionary<byte[], EntityBuffer> _buffer = new(ByteArrayComparer.Instance);
    private readonly Dictionary<byte[], EntityBuffer>.AlternateLookup<ReadOnlySpan<byte>> _bufferLookup;
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
        _bufferLookup = _buffer.GetAlternateLookup<ReadOnlySpan<byte>>();

        Directory.CreateDirectory(directory);

        // Scratch files from a crash mid-write were never published via the atomic rename — discard them.
        foreach (string tmp in Directory.EnumerateFiles(directory, "seg_*" + SegmentExtension + HistorySegment.TempSuffix))
            File.Delete(tmp);

        List<HistorySegment> loaded = [];
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "seg_*" + SegmentExtension))
            {
                if (!path.EndsWith(SegmentExtension, StringComparison.Ordinal)) continue; // guard against 8.3 glob matching *.hs.tmp
                loaded.Add(new HistorySegment(path));
            }
        }
        catch
        {
            // A mapping failure part-way through leaves the already-opened segments with no owner (the store instance is
            // never returned, so its Dispose never runs) — unmap them before propagating. Files are left on disk.
            foreach (HistorySegment segment in loaded) segment.Dispose();
            throw;
        }
        ReconcileOverlaps(loaded);
        _generation = new Generation(loaded.ToImmutableArray());

        if (loaded.Count > 0)
        {
            _anyDurable = _anyCompleted = true;
            _firstCompletedBlock = loaded[0].FromBlock;
            _durableMaxBlock = _lastCompletedBlock = loaded[^1].ToBlock;
        }
    }

    // A crash between publishing a merged segment and deleting its inputs leaves the covering segment plus the segments
    // it supersedes on disk, all matching the glob. Drop (and delete) every segment fully contained in a wider one so
    // the disjoint ascending-range invariant that reads rely on is restored; the covering (merged) segment is the newer,
    // authoritative copy. Runs at construction only (no concurrent readers), so eager dispose/delete is safe. Leaves
    // <paramref name="segments"/> ordered by ascending FromBlock.
    private static void ReconcileOverlaps(List<HistorySegment> segments)
    {
        // Covering-first order: for a shared FromBlock the widest range sorts ahead of the ranges nested inside it.
        segments.Sort(static (a, b) =>
        {
            int cmp = a.FromBlock.CompareTo(b.FromBlock);
            return cmp != 0 ? cmp : b.ToBlock.CompareTo(a.ToBlock);
        });

        List<HistorySegment> kept = new(segments.Count);
        ulong coveredTo = 0;
        bool any = false;
        foreach (HistorySegment segment in segments)
        {
            if (any && segment.ToBlock <= coveredTo)
            {
                segment.DisposeAndDelete(); // fully contained in an already-kept covering segment — an orphaned merge input
                continue;
            }
            if (any && segment.FromBlock <= coveredTo)
                throw new InvalidDataException(
                    $"History segment '{segment.Path}' partially overlaps a kept range up to block {coveredTo}; merges only ever produce nested or disjoint ranges, so this indicates on-disk corruption.");

            kept.Add(segment);
            coveredTo = segment.ToBlock;
            any = true;
        }

        segments.Clear();
        segments.AddRange(kept);
    }

    public void RecordChange(ulong block, scoped ReadOnlySpan<byte> flatKey, scoped ReadOnlySpan<byte> value)
    {
        // Drops re-delivery of an already-sealed block. Assumes forward-only capture: a block still buffered (sealed
        // block < block <= last completed) is never re-delivered within a session, so it can't double-append here.
        if (_anyDurable && block <= _durableMaxBlock) return;

        lock (_lock)
        {
            if (!_bufferLookup.TryGetValue(flatKey, out EntityBuffer? entity))
            {
                entity = new EntityBuffer();
                _buffer.Add(flatKey.ToArray(), entity);
            }
            entity.Blocks.Add(block);
            entity.Values.Add(value.IsEmpty ? [] : value.ToArray());
        }

        _bufferBytes += value.Length + sizeof(ulong) + sizeof(uint);
        StartStepIfNeeded(block);
    }

    public void CompleteBlock(ulong block)
    {
        if (_anyDurable && block <= _durableMaxBlock) return;

        if (!_anyCompleted || block > _lastCompletedBlock)
        {
            lock (_lock)
            {
                if (!_anyCompleted) _firstCompletedBlock = block;
                _lastCompletedBlock = block;
                _anyCompleted = true;
            }
        }
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
        Generation generation;
        lock (_lock)
        {
            int buffered = TryGetFromBuffer(block, flatKey, outBuffer, out foundAtBlock);
            if (buffered != -1) return buffered;
            generation = _generation;
            generation.AddRef();
        }

        try
        {
            ImmutableArray<HistorySegment> segments = generation.Segments;
            for (int s = segments.Length - 1; s >= 0; s--)
            {
                HistorySegment segment = segments[s];
                if (segment.FromBlock > block) continue;
                int written = segment.TryGetAt(flatKey, block, outBuffer, out foundAtBlock);
                if (written != -1) return written;
            }

            foundAtBlock = 0;
            return -1;
        }
        finally
        {
            generation.Release();
        }
    }

    public bool HasChangeInRange(scoped ReadOnlySpan<byte> flatKey, ulong afterExclusive, ulong atOrBefore)
    {
        if (afterExclusive >= atOrBefore) return false;

        Generation generation;
        lock (_lock)
        {
            if (_bufferLookup.TryGetValue(flatKey, out EntityBuffer? entity)
                && FloorIndex(entity.Blocks, atOrBefore) is int idx and >= 0
                && entity.Blocks[idx] > afterExclusive)
            {
                return true;
            }
            generation = _generation;
            generation.AddRef();
        }

        try
        {
            ImmutableArray<HistorySegment> segments = generation.Segments;
            for (int s = segments.Length - 1; s >= 0; s--)
            {
                HistorySegment segment = segments[s];
                if (segment.FromBlock > atOrBefore) continue;
                if (segment.ToBlock <= afterExclusive) break; // older segments are entirely at/below the lower bound
                if (segment.HasChangeInRange(flatKey, afterExclusive, atOrBefore)) return true;
            }
            return false;
        }
        finally
        {
            generation.Release();
        }
    }

    public bool CoversBlock(ulong block)
    {
        lock (_lock)
            return _anyCompleted && block >= _firstCompletedBlock && block <= _lastCompletedBlock;
    }

    private void StartStepIfNeeded(ulong block)
    {
        if (_bufferStarted) return;
        _bufferFirstBlock = block;
        _bufferStarted = true;
    }

    // Caller holds _lock.
    private int TryGetFromBuffer(ulong block, scoped ReadOnlySpan<byte> flatKey, Span<byte> outBuffer, out ulong foundAtBlock)
    {
        foundAtBlock = 0;
        if (!_bufferLookup.TryGetValue(flatKey, out EntityBuffer? entity)) return -1;

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

        // Building entries only reads the buffer; the single writer isn't mutating it here and concurrent readers are
        // read-only, so no lock is needed until the buffer is cleared. Keys are sorted here (the segment file requires
        // ascending keys) — the one-shot sort the Dictionary defers to seal time.
        ulong fromBlock = _bufferFirstBlock;
        byte[][] keys = [.. _buffer.Keys];
        Array.Sort(keys, static (a, b) => a.AsSpan().SequenceCompareTo(b));
        List<HistoryChangeEntry> entries = new(keys.Length);
        foreach (byte[] key in keys)
        {
            EntityBuffer entity = _buffer[key];
            entries.Add(new HistoryChangeEntry(key, [.. entity.Blocks], [.. entity.Values]));
        }

        string path = SegmentPath(fromBlock, toBlock);
        HistorySegment.Write(path, _keyLen, fromBlock, toBlock, entries);
        HistorySegment sealedSegment = new(path);

        // Publish the new segment and clear the buffer atomically so a reader sees the just-sealed step in exactly one
        // place: the buffer (before) or the segment (after), never neither.
        Generation outgoing;
        try
        {
            lock (_lock)
            {
                outgoing = _generation;
                _generation = new Generation(outgoing.Segments.Add(sealedSegment));
                _buffer.Clear();
            }
        }
        catch
        {
            // Publish failed (e.g. OOM growing the snapshot) before the buffer was cleared, so the step is still in
            // memory and re-seals later — unmap and delete the just-written file to match that "seal didn't happen" state.
            sealedSegment.DisposeAndDelete();
            throw;
        }
        outgoing.Release(); // a seal retires nothing; just drops the outgoing generation's store reference

        _durableMaxBlock = toBlock;
        _anyDurable = true;
        _bufferBytes = 0;
        _bufferStarted = false;

        MergeIfNeeded();
    }

    // Size-tiered merge: a segment's tier is derived from its block span (tier t spans [StepBlocks·F^t, StepBlocks·F^(t+1))).
    // Whenever `F` consecutive segments share a tier, the oldest `F` of them fuse into one segment of the next tier up.
    // A segment whose span reaches `MaxSegmentBlocks` is frozen and never merged again, so every block is rewritten at
    // most log_F(MaxSegmentBlocks/StepBlocks) times (bounded write amplification) and the file count stays logarithmic
    // in chain length plus one frozen file per MaxSegmentBlocks. Older segments are larger, so same-tier runs are always
    // contiguous and merges preserve the disjoint ascending-range invariant. Runs on the writer thread only.
    private void MergeIfNeeded()
    {
        while (TryFindMergeableRun(_generation.Segments, out int start, out int count))
            MergeRange(start, count);
    }

    // Oldest run of exactly `_mergeFanout` consecutive same-tier, non-frozen segments, if any.
    private bool TryFindMergeableRun(ImmutableArray<HistorySegment> segments, out int start, out int count)
    {
        int i = 0;
        while (i < segments.Length)
        {
            int tier = TierOf(segments[i]);
            if (tier == FrozenTier) { i++; continue; }

            int j = i + 1;
            while (j < segments.Length && TierOf(segments[j]) == tier) j++;
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
        // Reading the current segment array is safe without the lock: only the writer (this thread) replaces it.
        ImmutableArray<HistorySegment> segments = _generation.Segments;
        HistorySegment[] group = new HistorySegment[count];
        segments.CopyTo(start, group, 0, count);
        List<HistoryChangeEntry> merged = MergeEntries(group);

        ulong fromBlock = group[0].FromBlock;
        ulong toBlock = group[^1].ToBlock;
        string path = SegmentPath(fromBlock, toBlock);
        HistorySegment.Write(path, _keyLen, fromBlock, toBlock, merged);
        HistorySegment mergedSegment = new(path);

        Generation outgoing;
        try
        {
            // Build the successor before taking the lock so everything that can throw (the snapshot rebuild) happens
            // first: the in-lock swap then can't fail part-way and mark the still-live inputs retired against a
            // generation that never gets replaced.
            ImmutableArray<HistorySegment> next = segments.RemoveRange(start, count).Insert(start, mergedSegment);
            Generation successor = new(next);

            // Publish the merged set and retire the inputs against the outgoing generation: they stay mapped for any
            // reader still holding it and are unmapped + deleted only once that generation is fully released. The
            // reclaim runs outside the lock so file deletion never blocks a reader acquiring a snapshot.
            lock (_lock)
            {
                outgoing = _generation;
                outgoing.SetRetired(group);
                _generation = successor;
            }
        }
        catch
        {
            // Publish failed before the swap: the inputs are still live, so drop only the orphaned merged output.
            // ReconcileOverlaps would discard it on reopen anyway; deleting here keeps disk in step with memory.
            mergedSegment.DisposeAndDelete();
            throw;
        }
        outgoing.Release();
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

    public void Dispose()
    {
        Flush(); // seal the last partial step so it survives a restart
        // No concurrent readers per the IDisposable contract, so unmap the live segments directly (files are durable —
        // keep them). Merged-away segments were already deleted when their generation was released during operation.
        foreach (HistorySegment segment in _generation.Segments) segment.Dispose();
    }

    // An immutable published snapshot of the segment set with a reference count. The store holds one reference; each
    // in-flight reader holds one for the duration of its scan. Segments removed by a merge are recorded in
    // <see cref="_retired"/> and reclaimed (unmapped + file deleted) exactly when the count reaches zero — i.e. after
    // both the store has moved on and the last reader that captured this generation has finished — so a reader never
    // dereferences an unmapped file.
    private sealed class Generation(ImmutableArray<HistorySegment> segments)
    {
        public ImmutableArray<HistorySegment> Segments { get; } = segments;
        private HistorySegment[] _retired = [];
        private int _refCount = 1; // the store's reference

        public void AddRef() => Interlocked.Increment(ref _refCount);

        // Drops one reference; when the last one goes (store has moved on AND every reader that captured this generation
        // has finished) the segments retired against it are unmapped and their files deleted. Interlocked.Decrement
        // reaches zero exactly once, so the reclaim runs once and never races an in-flight read.
        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) != 0) return;
            foreach (HistorySegment segment in _retired) segment.DisposeAndDelete();
        }

        // Records the segments removed by the merge that produced the successor generation, to reclaim on last release.
        // Set under the store lock before this generation is replaced, so it is visible to any later Release.
        public void SetRetired(HistorySegment[] retired) => _retired = retired;
    }

    private sealed class EntityBuffer
    {
        public List<ulong> Blocks { get; } = [];
        public List<byte[]> Values { get; } = [];
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => HashKey(obj);

        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);
        public int GetHashCode(ReadOnlySpan<byte> alternate) => HashKey(alternate);
        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();

        private static int HashKey(scoped ReadOnlySpan<byte> key)
        {
            HashCode hash = new();
            hash.AddBytes(key);
            return hash.ToHashCode();
        }
    }
}
