// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Logging;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Pbt.Image;

/// <summary>External sort over transient on-disk runs held in the persisted-snapshot table format.</summary>
/// <remarks>
/// Records are buffered until the byte budget is reached, then sorted and streamed into one run table,
/// whose front-coded blocks shrink a record to its key suffix plus its value. <see cref="Read"/> merges
/// the runs k-way, collapsing equal keys; runs beyond <see cref="MaxFanIn"/> are folded into intermediate
/// run tables first, so the record count bounds the fan-in rather than the open file count. The merged
/// output is never materialized, and the runs outlive it, so <see cref="Read"/> may be called repeatedly.
/// </remarks>
internal sealed class PbtSortedSpool(string directory, int bufferBytes, ILogManager logManager, CancellationToken cancellationToken) : IDisposable
{
    /// <summary>The table format's key and value ceiling, and thus the merge's scratch size.</summary>
    private const int MaxRecordFieldLength = 255;

    /// <summary>Runs merged at once. Bounds the simultaneously mapped runs, not the record count.</summary>
    internal int MaxFanIn { get; init; } = 128;

    private readonly ILogger _logger = logManager.GetClassLogger<PbtSortedSpool>();
    private readonly List<string> _runs = [];
    // Every run file ever created, including those already folded away, so cleanup misses none.
    private readonly List<string> _scratch = [];
    private readonly List<Record> _records = [];
    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(bufferBytes, 2 * MaxRecordFieldLength));
    private int _buffered;
    private bool _completed;

    private readonly record struct Record(int Offset, int KeyLength, int ValueLength);

    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_completed) throw new InvalidOperationException("Sort already completed.");
        if (key.Length is 0 or > MaxRecordFieldLength || value.Length > MaxRecordFieldLength)
            throw new ArgumentException("Spool keys and values must be 1..255 bytes.", nameof(key));

        byte[] buffer = _buffer!;
        if (_buffered + key.Length + value.Length > buffer.Length) Flush();
        key.CopyTo(buffer.AsSpan(_buffered));
        value.CopyTo(buffer.AsSpan(_buffered + key.Length));
        _records.Add(new Record(_buffered, key.Length, value.Length));
        _buffered += key.Length + value.Length;
    }

    /// <summary>A fresh cursor over the merged records, in ascending key order.</summary>
    public Cursor Read()
    {
        Complete();
        return new Cursor(_runs, cancellationToken);
    }

    private void Complete()
    {
        if (_completed) return;
        Flush();
        _completed = true;
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

    private void Flush()
    {
        if (_records.Count == 0) return;
        byte[] buffer = _buffer!;
        Span<Record> records = CollectionsMarshal.AsSpan(_records);
        records.Sort((left, right) =>
            buffer.AsSpan(left.Offset, left.KeyLength).SequenceCompareTo(buffer.AsSpan(right.Offset, right.KeyLength)));
        _runs.Add(WriteRun(records));
        _records.Clear();
        _buffered = 0;
    }

    /// <summary>Stream sorted records into a new run table, collapsing equal keys.</summary>
    private string WriteRun(scoped ReadOnlySpan<Record> records)
    {
        byte[] buffer = _buffer!;
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
                    cancellationToken.ThrowIfCancellationRequested();
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
                using Cursor cursor = new(group, cancellationToken);
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
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        // Recorded before it is written so an aborted run is still cleaned up.
        _scratch.Add(path);
        return path;
    }

    internal static void RequireSameValue(scoped ReadOnlySpan<byte> left, scoped ReadOnlySpan<byte> right)
    {
        if (!left.SequenceEqual(right)) throw new InvalidDataException("Conflicting source leaves.");
    }

    public void Dispose()
    {
        if (_buffer is { } buffer)
        {
            _buffer = null;
            ArrayPool<byte>.Shared.Return(buffer);
        }
        foreach (string run in _scratch) File.Delete(run);
        _scratch.Clear();
        _runs.Clear();
    }

    /// <summary>Ascending cursor over the k-way merge of the spool's runs.</summary>
    internal sealed class Cursor : IDisposable
    {
        private readonly MappedByteFile[] _files;
        private readonly SortedTableEnumerator<MappedByteFile, NoOpPin>[] _sources;
        private readonly int[] _heap;
        private readonly byte[] _key = new byte[MaxRecordFieldLength];
        private readonly byte[] _value = new byte[MaxRecordFieldLength];
        private readonly byte[] _duplicate = new byte[MaxRecordFieldLength];
        private readonly CancellationToken _cancellationToken;
        private int _opened;
        private int _heapCount;
        private int _keyLength;
        private int _valueLength;

        public Cursor(List<string> runs, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _files = new MappedByteFile[runs.Count];
            _sources = new SortedTableEnumerator<MappedByteFile, NoOpPin>[runs.Count];
            _heap = new int[runs.Count];
            try
            {
                for (int index = 0; index < runs.Count; index++)
                {
                    MappedByteFile file = new(runs[index]);
                    _files[index] = file;
                    _sources[index] = new SortedTableEnumerator<MappedByteFile, NoOpPin>(in file, new Bound(0, file.Length));
                    // Only counted once the enumerator owns its buffer, so Dispose never frees a default one.
                    _opened = index + 1;
                    if (_sources[index].MoveNext(in file)) Push(index);
                }
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
            if (_heapCount == 0) return false;

            int source = _heap[0];
            _keyLength = Take(source, _key);
            _valueLength = ReadValue(source, _value);
            AdvanceRoot();

            // Equal keys from other runs carry the same record; collapse them and reject a real conflict.
            while (_heapCount != 0 && _sources[_heap[0]].CurrentKey.SequenceEqual(Key))
            {
                RequireSameValue(_duplicate.AsSpan(0, ReadValue(_heap[0], _duplicate)), Value);
                AdvanceRoot();
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

        /// <summary>Advance the run at the heap root, then restore the heap — dropping it once exhausted.</summary>
        private void AdvanceRoot()
        {
            int source = _heap[0];
            if (_sources[source].MoveNext(in _files[source])) SiftDown(0);
            else
            {
                _heap[0] = _heap[--_heapCount];
                if (_heapCount != 0) SiftDown(0);
            }
        }

        private void Push(int source)
        {
            int child = _heapCount++;
            _heap[child] = source;
            while (child != 0)
            {
                int parent = (child - 1) / 2;
                if (Compare(_heap[parent], _heap[child]) <= 0) break;
                (_heap[parent], _heap[child]) = (_heap[child], _heap[parent]);
                child = parent;
            }
        }

        private void SiftDown(int parent)
        {
            while (true)
            {
                int smallest = parent;
                int left = 2 * parent + 1;
                int right = left + 1;
                if (left < _heapCount && Compare(_heap[left], _heap[smallest]) < 0) smallest = left;
                if (right < _heapCount && Compare(_heap[right], _heap[smallest]) < 0) smallest = right;
                if (smallest == parent) return;
                (_heap[parent], _heap[smallest]) = (_heap[smallest], _heap[parent]);
                parent = smallest;
            }
        }

        /// <summary>Ascending by key; equal keys take the earlier run first, keeping the merge stable.</summary>
        private int Compare(int left, int right)
        {
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
