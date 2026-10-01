// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>
/// One shard's part of a log-backed batch: packs records into blocks, appends the blocks through a write buffer,
/// remembers which index slot each key resolves to, and publishes those slots once the records are durable on
/// disk. Nothing is visible to readers before <see cref="Publish"/>; a batch that is not committed is truncated away.
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

    // Latest record of a key within this batch: the block it is in, the slot it will occupy (NoSlot: a key new to
    // that generation, placed by probing at publish time) and, while its block is still open, where it starts in it.
    internal readonly record struct Pending(TrieNodeLogGeneration Generation, int Slot, long BlockOffset, int RecordStart);

    private readonly TrieNodeLogShard _shard = shard;
    private readonly ulong _version = version;
    private readonly List<TrieNodeLogGeneration> _pinned = pinned;
    private readonly List<(TrieNodeLogGeneration Generation, long StartFrontier)> _touched = [];
    private readonly Dictionary<ulong, Pending> _pending = pending;
    // Keys whose 64-bit hash collides with one already in _pending within this batch; each entry's key is
    // verified against its record, so a collision costs a read but never merges two keys.
    private readonly List<(ulong Hash, Pending Pending)> _collisions = [];
    private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(WriteBufferSize);
    private readonly byte[] _raw = ArrayPool<byte>.Shared.Rent(TrieNodeLogBlock.Size); // the open block
    private readonly byte[] _storedScratch = ArrayPool<byte>.Shared.Rent(TrieNodeLogBlock.MaxStoredLength);
    private readonly byte[] _rawScratch = ArrayPool<byte>.Shared.Rent(TrieNodeLogBlock.Size);
    private int _buffered;
    private int _rawLength;
    private bool _blockOpen;
    private long _blockOffset; // file offset the open block will be written at
    private TrieNodeLogGeneration? _current; // the generation the buffer appends to
    private int _pendingInsertsInCurrent;
    private bool _committed;
    private long _storedBytes;
    private readonly long[] _appendedBytesByColumn = new long[WriteBufferAdjuster.ColumnCount];

    public void Append(byte column, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool delete)
    {
        if (key.Length is 0 or > TrieNodeLogRecord.MaxKeyLength) throw new ArgumentOutOfRangeException(nameof(key), key.Length, "Unsupported trie node log key length");
        int length = TrieNodeLogRecord.HeaderLength + key.Length + value.Length;
        if (length > TrieNodeLogBlock.Size) throw new ArgumentOutOfRangeException(nameof(value), value.Length, "Trie node log record exceeds a block");

        TrieNodeLogGeneration generation = CurrentGeneration();
        if (!_blockOpen || _rawLength + length > TrieNodeLogBlock.Size) OpenBlock();
        ulong hash = TrieNodeLogRecord.Hash(column, key);

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
                if (!IsRecordOf(_collisions[i].Pending, column, key)) continue;
                collisionIndex = i;
                pending = _collisions[i].Pending;
                break;
            }
            // Keys never found in this batch: fall through to the index lookup below.
            if (collisionIndex < 0) pending = default;
        }

        ulong prev = 0;
        int slot = NoSlot;
        bool newToGeneration = true;
        if (pending.Generation is not null)
        {
            if (pending.Generation == generation)
            {
                slot = pending.Slot;
                newToGeneration = false;
            }

            if (pending.Generation == generation && pending.BlockOffset == _blockOffset)
            {
                // Still in the open block: retire the earlier record so a block never holds two live records of a key,
                // and inherit its prev link so chains skip it.
                prev = TrieNodeLogRecord.Read(_raw.AsSpan(pending.RecordStart)).Prev;
                _raw[pending.RecordStart] = TrieNodeLogRecord.Superseded;
            }
            else
            {
                prev = TrieNodeLogRecord.PackLocation(pending.Generation.Number, pending.BlockOffset);
            }
        }
        else
        {
            for (int i = _pinned.Count - 1; i >= 0; i--)
            {
                TrieNodeLogGeneration candidate = _pinned[i];
                if (!candidate.TryLocate(hash, column, key, _storedScratch, _rawScratch, out int index, out TrieNodeLogGeneration.BlockHit hit)) continue;
                prev = TrieNodeLogRecord.PackLocation(candidate.Number, hit.BlockOffset);
                if (candidate == generation)
                {
                    slot = index;
                    newToGeneration = false;
                }
                break;
            }
        }

        if (newToGeneration) _pendingInsertsInCurrent++;

        TrieNodeLogRecord header = new(delete ? TrieNodeLogRecord.Delete : TrieNodeLogRecord.Put, column, key.Length, value.Length, _version, prev);
        Span<byte> destination = _raw.AsSpan(_rawLength, length);
        header.Write(destination);
        key.CopyTo(destination[TrieNodeLogRecord.HeaderLength..]);
        value.CopyTo(destination[(TrieNodeLogRecord.HeaderLength + key.Length)..]);

        Pending record = new(generation, slot, _blockOffset, _rawLength);
        _rawLength += length;
        if (!collided) _pending[hash] = record;
        else if (collisionIndex >= 0) _collisions[collisionIndex] = (hash, record);
        else _collisions.Add((hash, record));
        _appendedBytesByColumn[column] += key.Length + value.Length;
    }

    /// <summary>Whether the record <paramref name="pending"/> points at was written for <paramref name="key"/>.</summary>
    private bool IsRecordOf(in Pending pending, byte column, ReadOnlySpan<byte> key)
    {
        ReadOnlySpan<byte> raw;
        if (pending.Generation == _current && _blockOpen && pending.BlockOffset == _blockOffset)
        {
            raw = _raw.AsSpan(0, _rawLength);
        }
        else
        {
            // A closed block is in the write buffer until flushed, then in the file.
            int rawLength = pending.Generation == _current && pending.BlockOffset >= _current.WriteFrontier
                ? TrieNodeLogBlock.Read(_buffer.AsSpan((int)(pending.BlockOffset - _current.WriteFrontier), _buffered - (int)(pending.BlockOffset - _current.WriteFrontier)), _rawScratch)
                : pending.Generation.ReadBlock(pending.BlockOffset, _storedScratch, _rawScratch);
            if (rawLength < 0) return false;
            raw = _rawScratch.AsSpan(0, rawLength);
        }

        return TrieNodeLogBlock.FindRecord(raw, column, key, out _) >= 0;
    }

    private TrieNodeLogGeneration CurrentGeneration()
    {
        TrieNodeLogGeneration? generation = _current ?? _shard.Active;
        if (generation is null || _shard.IsFull(generation, _pendingInsertsInCurrent, generation == _current ? _buffered + _rawLength : 0))
        {
            CloseBlock();
            FlushBuffer();
            generation = _shard.Roll();
            generation.TryAcquire();
            _pinned.Add(generation);
            _pendingInsertsInCurrent = 0;
        }

        if (_current != generation)
        {
            CloseBlock();
            FlushBuffer();
            _current = generation;
            _touched.Add((generation, generation.WriteFrontier));
        }

        return generation;
    }

    /// <summary>Closes the open block, if any, and starts a new one at the write position.</summary>
    private void OpenBlock()
    {
        CloseBlock();
        if (_buffered + TrieNodeLogBlock.MaxStoredLength > _buffer.Length) FlushBuffer();
        _blockOffset = _current!.WriteFrontier + _buffered;
        _blockOpen = true;
    }

    private void CloseBlock()
    {
        if (!_blockOpen) return;
        if (_rawLength > 0)
        {
            int written = TrieNodeLogBlock.Write(_buffer.AsSpan(_buffered), _raw.AsSpan(0, _rawLength), _shard.Compress);
            _buffered += written;
            _storedBytes += written;
            _rawLength = 0;
        }
        _blockOpen = false;
    }

    private void FlushBuffer()
    {
        if (_buffered == 0) return;
        _current!.Write(_current.WriteFrontier, _buffer.AsSpan(0, _buffered));
        _current.WriteFrontier += _buffered;
        _buffered = 0;
    }

    /// <summary>Writes a commit record to every generation this batch touched and fsyncs them; nothing is published yet.</summary>
    [SkipLocalsInit]
    public void MakeDurable()
    {
        try
        {
            Span<byte> commit = stackalloc byte[TrieNodeLogRecord.HeaderLength];
            TrieNodeLogRecord.CommitRecord(_version).Write(commit);
            Span<byte> commitBlock = stackalloc byte[TrieNodeLogBlock.HeaderLength + TrieNodeLogRecord.HeaderLength];
            int commitBlockLength = TrieNodeLogBlock.Write(commitBlock, commit, compress: false);

            foreach ((TrieNodeLogGeneration generation, _) in _touched)
            {
                if (generation == _current)
                {
                    if (!_blockOpen || _rawLength + commit.Length > TrieNodeLogBlock.Size) OpenBlock();
                    commit.CopyTo(_raw.AsSpan(_rawLength));
                    _rawLength += commit.Length;
                    CloseBlock();
                    FlushBuffer();
                }
                else
                {
                    // Rolled away from earlier in this batch; its blocks are already in the file.
                    generation.Write(generation.WriteFrontier, commitBlock[..commitBlockLength]);
                    generation.WriteFrontier += commitBlockLength;
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

    /// <summary>Points the index slots at the blocks written by this batch, making the records visible to readers whose version includes it.</summary>
    public void Publish()
    {
        foreach ((ulong hash, Pending pending) in _pending) PublishSlot(hash, pending);
        foreach ((ulong hash, Pending pending) in _collisions) PublishSlot(hash, pending);

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
        generation.Publish(index, hash, pending.BlockOffset);
    }

    /// <summary>Puts this batch's version into the metadata column batch, so RocksDB confirms it atomically with the state pointer.</summary>
    public void WriteVersion(IWriteOnlyKeyValueStore metadataBatch)
    {
        Span<byte> versionBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(versionBytes, _version);
        metadataBatch.PutSpan(_shard.VersionKey, versionBytes);
        Metrics.TrieNodeLogVersion[_shard.Label] = (long)_version;
    }

    /// <summary>Truncates the touched generations back to where this batch started; a no-op once published.</summary>
    public void Abort()
    {
        if (_committed) return;
        _buffered = 0;
        _rawLength = 0;
        _blockOpen = false;
        foreach ((TrieNodeLogGeneration generation, long startFrontier) in _touched) generation.Truncate(startFrontier);
        _touched.Clear();
        _pending.Clear();
        _collisions.Clear();
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        ArrayPool<byte>.Shared.Return(_raw);
        ArrayPool<byte>.Shared.Return(_storedScratch);
        ArrayPool<byte>.Shared.Return(_rawScratch);
        if (_committed)
        {
            _shard.OnBatchCommitted();
        }
        else
        {
            Abort();
            _shard.OnBatchAborted();
        }

        foreach (TrieNodeLogGeneration generation in _pinned) generation.Dispose();
        _pinned.Clear();
        _shard.ReturnPending(_pending);
    }
}
