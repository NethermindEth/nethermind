// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>
/// One shard's part of a log-backed batch: appends records through a write buffer, remembers which index slot
/// each key resolves to, and publishes those slots once the records are durable on disk. Nothing is visible
/// to readers before <see cref="Publish"/>; a batch that is not committed is truncated away.
/// </summary>
/// <remarks>
/// Commit is split so the owning <see cref="TrieNodeLog"/> can run it across shards in steps:
/// <see cref="MakeDurable"/> (commit records + fsync, in parallel), <see cref="Publish"/> (in parallel) and
/// <see cref="WriteVersion"/> (serially, into the RocksDB metadata batch).
/// </remarks>
internal sealed class TrieNodeLogWriteBatch(TrieNodeLogShard shard, ulong version, List<TrieNodeLogGeneration> pinned) : IDisposable
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

    public void Append(byte column, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool delete)
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
        TrieNodeLogGeneration? generation = _current ?? shard.Active;
        if (generation is null || shard.IsFull(generation, _pendingInsertsInCurrent, generation == _current ? _buffered : 0))
        {
            FlushBuffer();
            generation = shard.Roll();
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

    /// <summary>Writes a commit record to every generation this batch touched and fsyncs them; nothing is published yet.</summary>
    public void MakeDurable()
    {
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
        }
        catch
        {
            Abort();
            throw;
        }
    }

    /// <summary>Points the index slots at the records written by this batch, making them visible to readers whose version includes it.</summary>
    public void Publish()
    {
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

        for (int column = 0; column < _appendedBytesByColumn.Length; column++)
        {
            if (_appendedBytesByColumn[column] != 0) Metrics.TrieNodeLogAppendedBytes.AddBy(TrieNodeLogLabel.Column((byte)column), _appendedBytesByColumn[column]);
        }
        _committed = true;
    }

    /// <summary>Puts this batch's version into the metadata column batch, so RocksDB confirms it atomically with the state pointer.</summary>
    public void WriteVersion(IWriteOnlyKeyValueStore metadataBatch)
    {
        Span<byte> versionBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(versionBytes, version);
        metadataBatch.PutSpan(shard.VersionKey, versionBytes);
        Metrics.TrieNodeLogVersion[shard.Label] = (long)version;
    }

    /// <summary>Truncates the touched generations back to where this batch started; a no-op once published.</summary>
    public void Abort()
    {
        if (_committed) return;
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
            shard.OnBatchCommitted();
        }
        else
        {
            Abort();
            shard.OnBatchAborted();
        }

        foreach (TrieNodeLogGeneration generation in pinned) generation.Dispose();
        pinned.Clear();
    }
}
