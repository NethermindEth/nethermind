// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Nethermind.Logging;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Pbt.Image;

/// <summary>External sort over transient on-disk runs held in the persisted-snapshot table format.</summary>
/// <remarks>
/// Producers each hold a <see cref="Writer"/> over a private buffer segment, so several threads may spool
/// concurrently without contending on a shared buffer. A filled segment is handed to a background task that
/// sorts it and streams it into one run table, whose front-coded blocks shrink a record to its key suffix plus
/// its value, while the producer fills the next segment — the sort never blocks the walk that feeds it. Once
/// <see cref="PreMergeThreshold"/> runs exist they are folded into one intermediate run in the background too,
/// so the run count stays bounded during ingest rather than being folded in a pass of its own at the end.
/// <para><see cref="Read"/> merges the remaining runs k-way with a loser tree, collapsing equal keys; runs beyond
/// <see cref="MaxFanIn"/> are folded first, so the record count bounds the fan-in rather than the open file count.
/// The merged output is never materialized, and the runs outlive it, so <see cref="Read"/> may be called
/// repeatedly.</para>
/// </remarks>
internal sealed class PbtSortedSpool : IDisposable
{
    /// <summary>The table format's key and value ceiling, and thus the merge's scratch size.</summary>
    private const int MaxRecordFieldLength = 255;

    /// <summary>Most segments held by sorts rather than by producers, whatever the producer count.</summary>
    private const int MaxSpareSegments = 4;

    /// <summary>Runs merged at once. Bounds the simultaneously mapped runs, not the record count.</summary>
    internal int MaxFanIn { get; init; } = 128;

    /// <summary>Runs that accrue before one background round folds them into a single intermediate run.</summary>
    internal int PreMergeThreshold { get; init; } = 64;

    private readonly string _directory;
    private readonly int _segmentBytes;
    private readonly int _writerCount;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;
    /// <summary>Cancelled by the first background failure, so a producer blocked on the pool never waits forever.</summary>
    private readonly CancellationTokenSource _abort;
    private readonly SemaphoreSlim _segments;
    private readonly ConcurrentBag<Segment> _free = [];
    private readonly Lock _lock = new();
    // Sort and pre-merge tasks not yet observed to have finished; a pre-merge may enqueue another.
    private readonly List<Task> _pending = [];
    private readonly List<string> _runs = [];
    // Every run file ever created, including those already folded away, so cleanup misses none.
    private readonly List<string> _scratch = [];
    private ExceptionDispatchInfo? _fault;
    private int _openWriters;
    private bool _completed;

    /// <param name="bufferBytes">Bytes buffered per writer before a segment is sorted and spilled to a run.</param>
    /// <param name="writerCount">Producers that will spool concurrently; sizes the segment pool with them.</param>
    public PbtSortedSpool(string directory, int bufferBytes, int writerCount, ILogManager logManager,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(writerCount, 1);
        _directory = directory;
        _segmentBytes = Math.Max(bufferBytes, 2 * MaxRecordFieldLength);
        _writerCount = writerCount;
        _logger = logManager.GetClassLogger<PbtSortedSpool>();
        _cancellationToken = cancellationToken;
        _abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Strictly more segments than producers, so a spill always has somewhere to go and no writer can deadlock;
        // the spares are the sorts allowed to run behind the producers, and are what makes the pipeline a pipeline.
        // Segments are allocated on demand, so a spool that never fills its buffers never pays for the pool.
        _segments = new SemaphoreSlim(writerCount + Math.Clamp(writerCount / 2, 1, MaxSpareSegments));
    }

    private readonly record struct Record(int Offset, int KeyLength, int ValueLength);

    /// <summary>One producer's buffer segment and the records laid out in it.</summary>
    private sealed class Segment(byte[] buffer)
    {
        public byte[] Buffer => buffer;
        public List<Record> Records { get; } = [];
        public int Buffered { get; set; }

        public void Reset()
        {
            Records.Clear();
            Buffered = 0;
        }
    }

    /// <summary>A producer's handle on the spool. One per thread; a single handle is not itself thread-safe.</summary>
    /// <remarks>Disposing spills whatever the handle still holds, so every writer must be disposed before
    /// <see cref="Read"/> — records left in a live segment would silently be missing from the merge.</remarks>
    internal sealed class Writer(PbtSortedSpool spool) : IDisposable
    {
        private Segment? _segment;
        private bool _closed;

        public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
        {
            spool._cancellationToken.ThrowIfCancellationRequested();
            spool.ThrowIfFaulted();
            if (key.Length is 0 or > MaxRecordFieldLength || value.Length > MaxRecordFieldLength)
                throw new ArgumentException("Spool keys and values must be 1..255 bytes.", nameof(key));

            Segment segment = _segment ??= spool.Rent();
            if (segment.Buffered + key.Length + value.Length > segment.Buffer.Length)
            {
                spool.Submit(segment);
                segment = _segment = spool.Rent();
            }
            key.CopyTo(segment.Buffer.AsSpan(segment.Buffered));
            value.CopyTo(segment.Buffer.AsSpan(segment.Buffered + key.Length));
            segment.Records.Add(new Record(segment.Buffered, key.Length, value.Length));
            segment.Buffered += key.Length + value.Length;
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            if (_segment is { } segment)
            {
                _segment = null;
                spool.Submit(segment);
            }
            Interlocked.Decrement(ref spool._openWriters);
        }
    }

    /// <summary>A handle for one producer. Writers must all be disposed before the spool is read.</summary>
    public Writer CreateWriter()
    {
        lock (_lock)
        {
            if (_completed) throw new InvalidOperationException("Sort already completed.");
            if (_openWriters == _writerCount)
                throw new InvalidOperationException("Spool has no free producer slot; it was sized for fewer writers.");
            _openWriters++;
        }
        return new Writer(this);
    }

    /// <summary>A fresh cursor over the merged records, in ascending key order.</summary>
    public Cursor Read()
    {
        Complete();
        return new Cursor(_runs, _cancellationToken);
    }

    /// <remarks>Waits on the abort token rather than the caller's: a background sort that failed will never
    /// return its segment, so a producer waiting on the caller's token alone would block for good.</remarks>
    private Segment Rent()
    {
        try
        {
            _segments.Wait(_abort.Token);
        }
        catch (OperationCanceledException)
        {
            // Surface the sort's own exception in preference to the cancellation it raised here.
            ThrowIfFaulted();
            throw;
        }
        return _free.TryTake(out Segment? segment) ? segment : new Segment(GC.AllocateUninitializedArray<byte>(_segmentBytes));
    }

    private void Return(Segment segment)
    {
        segment.Reset();
        _free.Add(segment);
        _segments.Release();
    }

    /// <summary>Hand a filled segment to a background sort, or take it straight back when it holds nothing.</summary>
    private void Submit(Segment segment)
    {
        if (segment.Records.Count == 0)
        {
            Return(segment);
            return;
        }
        Enqueue(() =>
        {
            try { Publish(WriteRun(segment)); }
            finally { Return(segment); }
        });
    }

    private void Enqueue(Action work)
    {
        Task task = Task.Run(() =>
        {
            try
            {
                work();
            }
            catch (Exception exception)
            {
                // Faults are surfaced from Add, Rent and Complete rather than from the task, whose only observer
                // would be a Dispose that must not throw. Aborting wakes a producer already blocked on the pool.
                Interlocked.CompareExchange(ref _fault, ExceptionDispatchInfo.Capture(exception), null);
                _abort.Cancel();
            }
        }, CancellationToken.None);

        lock (_lock)
        {
            _pending.RemoveAll(pending => pending.IsCompleted);
            _pending.Add(task);
        }
    }

    /// <summary>Record a finished run, folding a full round of them in the background once enough accrue.</summary>
    private void Publish(string run)
    {
        List<string> round;
        lock (_lock)
        {
            _runs.Add(run);
            if (_completed || _runs.Count < PreMergeThreshold) return;
            round = _runs.GetRange(0, PreMergeThreshold);
            _runs.RemoveRange(0, PreMergeThreshold);
        }
        Enqueue(() =>
        {
            Publish(MergeRuns(round));
            // The round was detached under the lock, so no cursor and no other fold can be holding its files.
            foreach (string folded in round) File.Delete(folded);
        });
    }

    /// <remarks>Freezing precedes the drain: a pre-merge that detaches runs after the fold below has started
    /// would drop them from the merged output without any error to show for it.</remarks>
    private void Complete()
    {
        lock (_lock)
        {
            if (_completed) return;
            if (_openWriters != 0) throw new InvalidOperationException("Spool writers must be disposed before reading.");
            _completed = true;
        }
        WaitForPending();
        ThrowIfFaulted();
        // Folding is the one phase no reader loop observes, so each round reports itself.
        while (_runs.Count > MaxFanIn)
        {
            Stopwatch folding = Stopwatch.StartNew();
            if (_logger.IsInfo) _logger.Info($"PBT spool: folding {_runs.Count:N0} sorted runs.");
            List<string> folded = [];
            for (int start = 0; start < _runs.Count; start += MaxFanIn)
            {
                List<string> group = _runs.GetRange(start, Math.Min(MaxFanIn, _runs.Count - start));
                // A lone trailing run is already a sorted run; rewriting it would only cost a pass.
                if (group.Count == 1) { folded.Add(group[0]); continue; }
                folded.Add(MergeRuns(group));
                foreach (string merged in group) File.Delete(merged);
            }
            _runs.Clear();
            _runs.AddRange(folded);
            if (_logger.IsInfo) _logger.Info($"PBT spool: folded into {_runs.Count:N0} runs in {folding.Elapsed:hh\\:mm\\:ss}.");
        }
    }

    /// <remarks>A pre-merge enqueues its own follow-up, so draining repeats until a round adds nothing.</remarks>
    private void WaitForPending()
    {
        while (true)
        {
            Task[] pending;
            lock (_lock)
            {
                _pending.RemoveAll(task => task.IsCompleted);
                if (_pending.Count == 0) return;
                pending = [.. _pending];
            }
            Task.WaitAll(pending, CancellationToken.None);
        }
    }

    private void ThrowIfFaulted() => Volatile.Read(ref _fault)?.Throw();

    /// <summary>Sort a segment's records by key, then stream them into a new run table, collapsing equal keys.</summary>
    private string WriteRun(Segment segment)
    {
        byte[] buffer = segment.Buffer;
        Span<Record> records = CollectionsMarshal.AsSpan(segment.Records);
        records.Sort(new RecordKeyComparer(buffer));

        string path = NewRun();
        ArenaBufferWriter writer = new(File.Create(path), firstOffset: 0);
        try
        {
            SortedTableBuilder<ArenaBufferWriter> table = new(ref writer);
            try
            {
                Record previous = default;
                for (int index = 0; index < records.Length; index++)
                {
                    _abort.Token.ThrowIfCancellationRequested();
                    Record record = records[index];
                    ReadOnlySpan<byte> key = buffer.AsSpan(record.Offset, record.KeyLength);
                    ReadOnlySpan<byte> value = buffer.AsSpan(record.Offset + record.KeyLength, record.ValueLength);
                    if (index != 0 && key.SequenceEqual(buffer.AsSpan(previous.Offset, previous.KeyLength)))
                    {
                        RequireSameValue(value, buffer.AsSpan(previous.Offset + previous.KeyLength, previous.ValueLength));
                        continue;
                    }
                    previous = record;
                    table.Add(key, value);
                }
                table.Build();
            }
            finally { table.Dispose(); }
        }
        finally { writer.Dispose(); }
        return path;
    }

    /// <summary>Orders a segment's records by the key bytes they point at.</summary>
    private readonly struct RecordKeyComparer(byte[] buffer) : IComparer<Record>
    {
        public int Compare(Record left, Record right) =>
            buffer.AsSpan(left.Offset, left.KeyLength).SequenceCompareTo(buffer.AsSpan(right.Offset, right.KeyLength));
    }

    /// <summary>Merge several runs into one new run table, collapsing equal keys.</summary>
    private string MergeRuns(List<string> group)
    {
        string path = NewRun();
        ArenaBufferWriter writer = new(File.Create(path), firstOffset: 0);
        try
        {
            SortedTableBuilder<ArenaBufferWriter> table = new(ref writer);
            try
            {
                using Cursor cursor = new(group, _abort.Token);
                while (cursor.MoveNext()) table.Add(cursor.Key, cursor.Value);
                table.Build();
            }
            finally { table.Dispose(); }
        }
        finally { writer.Dispose(); }
        return path;
    }

    private string NewRun()
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        // Recorded before it is written so an aborted run is still cleaned up.
        lock (_lock) _scratch.Add(path);
        return path;
    }

    internal static void RequireSameValue(scoped ReadOnlySpan<byte> left, scoped ReadOnlySpan<byte> right)
    {
        if (!left.SequenceEqual(right)) throw new InvalidDataException("Conflicting source leaves.");
    }

    /// <remarks>Aborts, then joins the background tasks, and only then reclaims: one of them may still be
    /// writing a run file, or holding a segment, that this would otherwise delete or reclaim underneath it.</remarks>
    public void Dispose()
    {
        lock (_lock) _completed = true;
        _abort.Cancel();
        WaitForPending();
        _free.Clear();
        foreach (string run in _scratch) File.Delete(run);
        _scratch.Clear();
        _runs.Clear();
        _segments.Dispose();
        _abort.Dispose();
    }

    /// <summary>Ascending cursor over the k-way merge of the spool's runs.</summary>
    /// <remarks>
    /// The runs are merged with a tournament (loser) tree: each internal node holds the loser of a match and the
    /// overall winner ends up at <c>_tree[0]</c>, so advancing replays one leaf's path to the root — one key
    /// comparison per level, against the binary heap's two. Leaf index <c>_k</c> is the sentinel: it seeds the
    /// tree, sorting below every real head, and marks an exhausted run, sorting above every real head.
    /// </remarks>
    internal sealed class Cursor : IDisposable
    {
        private readonly MappedByteFile[] _files;
        private readonly SortedTableEnumerator<MappedByteFile, NoOpPin>[] _sources;
        private readonly bool[] _exhausted;
        private readonly int[] _tree;
        private readonly int _k;
        private readonly byte[] _key = new byte[MaxRecordFieldLength];
        private readonly byte[] _value = new byte[MaxRecordFieldLength];
        private readonly byte[] _duplicate = new byte[MaxRecordFieldLength];
        private readonly CancellationToken _cancellationToken;
        private int _opened;
        private int _keyLength;
        private int _valueLength;

        public Cursor(List<string> runs, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _k = runs.Count;
            _files = new MappedByteFile[_k];
            _sources = new SortedTableEnumerator<MappedByteFile, NoOpPin>[_k];
            _exhausted = new bool[_k];
            _tree = new int[_k];
            try
            {
                for (int index = 0; index < _k; index++)
                {
                    MappedByteFile file = new(runs[index]);
                    _files[index] = file;
                    _sources[index] = new SortedTableEnumerator<MappedByteFile, NoOpPin>(in file, new Bound(0, file.Length));
                    // Only counted once the enumerator owns its buffer, so Dispose never frees a default one.
                    _opened = index + 1;
                    _exhausted[index] = !_sources[index].MoveNext(in file);
                }
                for (int index = 0; index < _k; index++) _tree[index] = _k;
                for (int index = _k - 1; index >= 0; index--) Adjust(index);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public ReadOnlySpan<byte> Key => _key.AsSpan(0, _keyLength);
        public ReadOnlySpan<byte> Value => _value.AsSpan(0, _valueLength);

        public bool MoveNext()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_k == 0) return false;

            int winner = _tree[0];
            if (winner == _k || _exhausted[winner]) return false;

            _keyLength = Take(winner, _key);
            _valueLength = ReadValue(winner, _value);
            Advance(winner);

            // Equal keys from other runs carry the same record; collapse them and reject a real conflict.
            while (true)
            {
                int current = _tree[0];
                if (current == _k || _exhausted[current] || !_sources[current].CurrentKey.SequenceEqual(Key)) break;
                RequireSameValue(_duplicate.AsSpan(0, ReadValue(current, _duplicate)), Value);
                Advance(current);
            }
            return true;
        }

        private int Take(int source, byte[] destination)
        {
            ReadOnlySpan<byte> key = _sources[source].CurrentKey;
            key.CopyTo(destination);
            return key.Length;
        }

        private int ReadValue(int source, byte[] destination)
        {
            Bound value = _sources[source].CurrentValue;
            int length = checked((int)value.Length);
            if (!_files[source].TryRead(value.Offset, destination.AsSpan(0, length)))
                throw new InvalidDataException("Truncated spool run value.");
            return length;
        }

        /// <summary>Advance one run, then replay its leaf's path so the tree names the next winner.</summary>
        private void Advance(int source)
        {
            if (!_sources[source].MoveNext(in _files[source])) _exhausted[source] = true;
            Adjust(source);
        }

        private void Adjust(int source)
        {
            for (int parent = (source + _k) >> 1; parent > 0; parent >>= 1)
                if (CompareHeads(source, _tree[parent]) > 0) (source, _tree[parent]) = (_tree[parent], source);
            _tree[0] = source;
        }

        /// <summary>Ascending by key; equal keys take the earlier run first, keeping the merge stable.</summary>
        private int CompareHeads(int left, int right)
        {
            if (left == _k) return right == _k ? 0 : -1;
            if (right == _k) return 1;
            if (_exhausted[left] || _exhausted[right])
            {
                if (_exhausted[left] && _exhausted[right]) return left.CompareTo(right);
                return _exhausted[left] ? 1 : -1;
            }
            int order = _sources[left].CurrentKey.SequenceCompareTo(_sources[right].CurrentKey);
            return order != 0 ? order : left.CompareTo(right);
        }

        public void Dispose()
        {
            for (int index = 0; index < _opened; index++) _sources[index].Dispose();
            _opened = 0;
            for (int index = 0; index < _files.Length; index++) _files[index]?.Dispose();
        }
    }
}
