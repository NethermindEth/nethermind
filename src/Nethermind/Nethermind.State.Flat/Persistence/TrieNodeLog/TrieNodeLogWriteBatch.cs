// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>
/// One log-backed batch: appends records through a write buffer, remembers which index slot each key resolves
/// to, and publishes those slots in <see cref="Commit"/> once the records are durable on disk. Nothing is
/// visible to readers before <see cref="Commit"/>; a batch that is not committed is truncated away.
/// </summary>
internal sealed class TrieNodeLogWriteBatch(TrieNodeLog log, ulong version, List<TrieNodeLogGeneration> pinned) : ITrieNodeLog.IWriteBatch
{
    private const int WriteBufferSize = 1024 * 1024;
    private const int NoSlot = -1;

    // Latest record of a key within this batch and the slot it will occupy (NoSlot: a key new to that generation,
    // placed by probing at publish time).
    private readonly record struct Pending(TrieNodeLogGeneration Generation, int Slot, long Offset);

    private readonly List<(TrieNodeLogGeneration Generation, long StartFrontier)> _touched = [];
    private readonly Dictionary<ulong, Pending> _pending = [];
    private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(WriteBufferSize);
    private readonly byte[] _probeBuffer = new byte[TrieNodeLogRecord.HeaderLength + TrieNodeLogRecord.MaxKeyLength];
    private int _buffered;
    private TrieNodeLogGeneration? _current; // the generation the buffer appends to
    private int _pendingInsertsInCurrent;
    private bool _committed;
    private readonly long[] _appendedBytesByColumn = new long[WriteBufferAdjuster.ColumnCount];

    public IWriteBatch Wrap(FlatDbColumns column, IWriteBatch inner) => log.Covers(column) ? new Column(this, (byte)column) : inner;

    private void Append(byte column, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool delete)
    {
        if (key.Length is 0 or > TrieNodeLogRecord.MaxKeyLength) throw new ArgumentOutOfRangeException(nameof(key), key.Length, "Unsupported trie node log key length");

        TrieNodeLogGeneration generation = CurrentGeneration();
        ulong hash = TrieNodeLogRecord.Hash(column, key);

        ulong prev = 0;
        int slot = NoSlot;
        bool newToGeneration = true;
        if (_pending.TryGetValue(hash, out Pending pending))
        {
            prev = TrieNodeLogRecord.PackLocation(pending.Generation.Number, pending.Offset);
            if (pending.Generation == generation)
            {
                slot = pending.Slot;
                newToGeneration = false;
            }
        }
        else
        {
            for (int i = pinned.Count - 1; i >= 0; i--)
            {
                TrieNodeLogGeneration candidate = pinned[i];
                if (!candidate.TryLocate(hash, column, key, _probeBuffer, out _, out int index, out long offset, out _)) continue;
                prev = TrieNodeLogRecord.PackLocation(candidate.Number, offset);
                if (candidate == generation)
                {
                    slot = index;
                    newToGeneration = false;
                }
                break;
            }
        }

        if (newToGeneration) _pendingInsertsInCurrent++;

        TrieNodeLogRecord header = new(delete ? TrieNodeLogRecord.Delete : TrieNodeLogRecord.Put, column, key.Length, value.Length, version, prev);
        long recordOffset = Reserve(header.Length);
        Span<byte> destination = _buffer.AsSpan(_buffered, header.Length);
        header.Write(destination);
        key.CopyTo(destination[TrieNodeLogRecord.HeaderLength..]);
        value.CopyTo(destination[(TrieNodeLogRecord.HeaderLength + key.Length)..]);
        _buffered += header.Length;

        _pending[hash] = new Pending(generation, slot, recordOffset);
        _appendedBytesByColumn[column] += key.Length + value.Length;
    }

    private TrieNodeLogGeneration CurrentGeneration()
    {
        TrieNodeLogGeneration? generation = _current ?? log.Active;
        if (generation is null || log.IsFull(generation, _pendingInsertsInCurrent, generation == _current ? _buffered : 0))
        {
            FlushBuffer();
            generation = log.Roll();
            generation.TryAcquire();
            pinned.Add(generation);
            _pendingInsertsInCurrent = 0;
        }

        if (_current != generation)
        {
            FlushBuffer();
            _current = generation;
            _touched.Add((generation, generation.WriteFrontier));
        }

        return generation;
    }

    /// <summary>Makes room for <paramref name="length"/> bytes in the buffer and returns the file offset they will land at.</summary>
    private long Reserve(int length)
    {
        if (_buffered + length > _buffer.Length) FlushBuffer();
        if (length > _buffer.Length) throw new ArgumentOutOfRangeException(nameof(length), length, "Trie node log record exceeds the write buffer");
        return _current!.WriteFrontier + _buffered;
    }

    private void FlushBuffer()
    {
        if (_buffered == 0) return;
        _current!.Write(_current.WriteFrontier, _buffer.AsSpan(0, _buffered));
        _current.WriteFrontier += _buffered;
        _buffered = 0;
    }

    public void Commit(IWriteOnlyKeyValueStore metadataBatch)
    {
        long sw = Stopwatch.GetTimestamp();
        try
        {
            Span<byte> commit = stackalloc byte[TrieNodeLogRecord.HeaderLength];
            TrieNodeLogRecord.CommitRecord(version).Write(commit);
            foreach ((TrieNodeLogGeneration generation, _) in _touched)
            {
                if (generation == _current)
                {
                    Reserve(commit.Length);
                    commit.CopyTo(_buffer.AsSpan(_buffered));
                    _buffered += commit.Length;
                    FlushBuffer();
                }
                else
                {
                    generation.Write(generation.WriteFrontier, commit);
                    generation.WriteFrontier += commit.Length;
                }
                generation.Fsync();
            }

            foreach ((ulong hash, Pending pending) in _pending)
            {
                TrieNodeLogGeneration generation = pending.Generation;
                int index = pending.Slot;
                if (index == NoSlot)
                {
                    // A key new to the generation: any occupied slot on the probe path holds a different key
                    // (verified when the record was appended, or a sibling of this batch with another hash).
                    for (index = generation.HomeIndex(hash); generation.ReadSlot(index) != 0; index = generation.NextIndex(index)) { }
                }
                generation.Publish(index, hash, pending.Offset);
            }

            foreach ((TrieNodeLogGeneration generation, _) in _touched) generation.PublishFrontier(generation.WriteFrontier);

            Span<byte> versionBytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(versionBytes, version);
            metadataBatch.PutSpan(TrieNodeLog.VersionKey, versionBytes);
            _committed = true;
        }
        catch
        {
            Abort();
            throw;
        }

        for (int column = 0; column < _appendedBytesByColumn.Length; column++)
        {
            if (_appendedBytesByColumn[column] != 0) Metrics.TrieNodeLogAppendedBytes.AddBy(TrieNodeLogLabel.Column((byte)column), _appendedBytesByColumn[column]);
        }
        Metrics.TrieNodeLogVersion = (long)version;
        Metrics.TrieNodeLogCommitTime.Observe(Stopwatch.GetTimestamp() - sw);
    }

    private void Abort()
    {
        _buffered = 0;
        foreach ((TrieNodeLogGeneration generation, long startFrontier) in _touched) generation.Truncate(startFrontier);
        _touched.Clear();
        _pending.Clear();
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        if (_committed)
        {
            log.OnBatchCommitted();
        }
        else
        {
            Abort();
            log.OnBatchAborted();
        }

        foreach (TrieNodeLogGeneration generation in pinned) generation.Dispose();
        pinned.Clear();
    }

    private sealed class Column(TrieNodeLogWriteBatch batch, byte column) : IWriteBatch
    {
        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (value is null) Remove(key);
            else batch.Append(column, key, value, delete: false);
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => batch.Append(column, key, value, delete: false);

        public void Remove(ReadOnlySpan<byte> key) => batch.Append(column, key, default, delete: true);

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        // The owning persistence batch drives commit and disposal.
        public void Dispose() { }
    }
}
