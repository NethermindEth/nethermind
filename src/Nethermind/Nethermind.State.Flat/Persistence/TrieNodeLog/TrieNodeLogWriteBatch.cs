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
internal sealed class TrieNodeLogWriteBatch(TrieNodeLogShard shard, ulong version, List<TrieNodeLogGeneration> pinned, Dictionary<ulong, TrieNodeLogWriteBatch.Pending> pending) : IDisposable
{
    private const int WriteBufferSize = 1024 * 1024;
    private const int NoSlot = -1;

    // Latest record of a key within this batch and the slot it will occupy (NoSlot: a key new to that generation,
    // placed by probing at publish time).
    internal readonly record struct Pending(TrieNodeLogGeneration Generation, int Slot, long Offset);

    private readonly List<(TrieNodeLogGeneration Generation, long StartFrontier)> _touched = [];
    private readonly Dictionary<ulong, Pending> _pending = pending;
    // Keys whose 64-bit hash collides with one already in _pending within this batch; each entry's key is
    // verified against its record, so a collision costs a read but never merges two keys.
    private readonly List<Pending> _collisions = [];
    private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(WriteBufferSize);
    private readonly byte[] _probeBuffer = new byte[TrieNodeLogRecord.HeaderLength + TrieNodeLogRecord.MaxKeyLength];
    private int _buffered;
    private TrieNodeLogGeneration? _current; // the generation the buffer appends to
    private int _pendingInsertsInCurrent;
    private bool _committed;
    private long _storedBytes;
    private readonly long[] _appendedBytesByColumn = new long[WriteBufferAdjuster.ColumnCount];

    public void Append(byte column, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool delete)
    {
        if (key.Length is 0 or > TrieNodeLogRecord.MaxKeyLength) throw new ArgumentOutOfRangeException(nameof(key), key.Length, "Unsupported trie node log key length");
        if (value.Length > TrieNodeLogRecord.MaxValueLength) throw new ArgumentOutOfRangeException(nameof(value), value.Length, "Unsupported trie node log value length");

        TrieNodeLogGeneration generation = CurrentGeneration();
        ulong hash = TrieNodeLogRecord.Hash(column, key);

        ulong prev = 0;
        int slot = NoSlot;
        bool newToGeneration = true;
        bool collided = _pending.TryGetValue(hash, out Pending pending);
        int collisionIndex = -1;
        if (collided && IsRecordOf(pending, column, key))
        {
            collided = false;
        }
        else if (collided)
        {
            for (int i = 0; i < _collisions.Count; i++)
            {
                if (!IsRecordOf(_collisions[i], column, key)) continue;
                collisionIndex = i;
                pending = _collisions[i];
                break;
            }
            // Keys never found in this batch: fall through to the index lookup below.
            if (collisionIndex < 0) pending = default;
        }

        if (pending.Generation is not null)
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

        Pending record = new(generation, slot, recordOffset);
        if (!collided) _pending[hash] = record;
        else if (collisionIndex >= 0) _collisions[collisionIndex] = record;
        else _collisions.Add(record);
        _appendedBytesByColumn[column] += key.Length + value.Length;
    }

    /// <summary>Whether the record <paramref name="pending"/> points at was written for <paramref name="key"/>; it is read from the write buffer while still there.</summary>
    private bool IsRecordOf(in Pending pending, byte column, ReadOnlySpan<byte> key)
    {
        ReadOnlySpan<byte> record;
        if (pending.Generation == _current && pending.Offset >= _current.WriteFrontier)
        {
            record = _buffer.AsSpan((int)(pending.Offset - _current.WriteFrontier), _buffered - (int)(pending.Offset - _current.WriteFrontier));
        }
        else
        {
            int read = pending.Generation.ReadAt(pending.Offset, _probeBuffer);
            record = _probeBuffer.AsSpan(0, read);
        }

        if (record.Length < TrieNodeLogRecord.HeaderLength) return false;
        TrieNodeLogRecord header = TrieNodeLogRecord.Read(record);
        return header.Column == column && header.KeyLength == key.Length && record.Length >= TrieNodeLogRecord.HeaderLength + key.Length
            && record.Slice(TrieNodeLogRecord.HeaderLength, key.Length).SequenceEqual(key);
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
        _storedBytes += _buffered;
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
        foreach ((ulong hash, Pending pending) in _pending) PublishSlot(hash, pending);
        foreach (Pending pending in _collisions) PublishSlot(HashOfRecord(pending), pending);

        foreach ((TrieNodeLogGeneration generation, _) in _touched) generation.PublishFrontier(generation.WriteFrontier);

        for (int column = 0; column < _appendedBytesByColumn.Length; column++)
        {
            if (_appendedBytesByColumn[column] != 0) Metrics.TrieNodeLogAppendedBytes.AddBy(TrieNodeLogLabel.Column((byte)column), _appendedBytesByColumn[column]);
        }
        Metrics.AddTrieNodeLogStoredBytes(_storedBytes);
        _committed = true;
    }

    private static void PublishSlot(ulong hash, in Pending pending)
    {
        TrieNodeLogGeneration generation = pending.Generation;
        int index = pending.Slot;
        if (index == NoSlot)
        {
            // A key new to the generation: any occupied slot on the probe path holds a different key (verified when
            // the record was appended, or a sibling of this batch, which lands in a free slot of its own even when
            // the hashes collide).
            for (index = generation.HomeIndex(hash); generation.ReadSlot(index) != 0; index = generation.NextIndex(index)) { }
        }
        generation.Publish(index, hash, pending.Offset);
    }

    private ulong HashOfRecord(in Pending pending)
    {
        int read = pending.Generation.ReadAt(pending.Offset, _probeBuffer);
        if (read < TrieNodeLogRecord.HeaderLength) throw new IOException($"Short read of trie node log record at {pending.Generation.Path}:{pending.Offset}");
        TrieNodeLogRecord header = TrieNodeLogRecord.Read(_probeBuffer);
        if (read < TrieNodeLogRecord.HeaderLength + header.KeyLength) throw new IOException($"Short read of trie node log record at {pending.Generation.Path}:{pending.Offset}");
        return TrieNodeLogRecord.Hash(header.Column, _probeBuffer.AsSpan(TrieNodeLogRecord.HeaderLength, header.KeyLength));
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
        _collisions.Clear();
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
        shard.ReturnPending(_pending);
    }
}
