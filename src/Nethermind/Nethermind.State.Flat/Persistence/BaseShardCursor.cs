// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Ascending key cursor over one entity's base shard tables, restricted to
/// <c>[startInclusive, endExclusive)</c>: walks the shards in order starting at the shard of
/// <c>startInclusive</c> (seeking within it), and stops at the first key ≥ <c>endExclusive</c> — shards
/// cover contiguous ascending key ranges, so every later record is out of range too.
/// </summary>
/// <remarks>
/// The caller (a <see cref="BaseTableView"/> holding leases on the shard files) must outlive the cursor;
/// <see cref="CurrentKey"/> is valid until the next <see cref="MoveNext"/> and <see cref="CurrentValue"/>
/// points into the shard's mmap, valid for the view's lifetime.
/// </remarks>
internal sealed unsafe class BaseShardCursor : IDisposable
{
    private readonly BaseTableView.ShardTable?[] _shards;
    private readonly byte[] _startInclusive;
    private readonly byte[] _endExclusive;
    private readonly int _startShard;
    private int _shardIdx;
    private SortedTableEnumerator<MmapByteReader, NoOpPin> _enumerator;
    private BaseTableView.ShardTable? _active;
    // True while records may still precede _startInclusive — only within the block-granular seek's
    // first data block; cleared at the first in-range key.
    private bool _checkStart;
    private bool _exhausted;

    internal BaseShardCursor(BaseTableView.ShardTable?[] shards, byte[] startInclusive, byte[] endExclusive)
    {
        _shards = shards;
        _startInclusive = startInclusive;
        _endExclusive = endExclusive;
        _startShard = BaseTableView.ShardOf(startInclusive, shards.Length);
        _shardIdx = _startShard - 1; // advanced by the first TryOpenNextShard
    }

    public bool MoveNext()
    {
        if (_exhausted) return false;
        while (true)
        {
            if (_active is null && !TryOpenNextShard())
            {
                _exhausted = true;
                return false;
            }

            MmapByteReader reader = new(_active!.File.BasePtr, _active.Length);
            if (!_enumerator.MoveNext(in reader))
            {
                CloseActive();
                continue;
            }

            ReadOnlySpan<byte> key = _enumerator.CurrentKey;
            if (_checkStart)
            {
                if (key.SequenceCompareTo(_startInclusive) < 0) continue;
                _checkStart = false;
            }

            if (key.SequenceCompareTo(_endExclusive) >= 0)
            {
                CloseActive();
                _exhausted = true;
                return false;
            }

            return true;
        }
    }

    private bool TryOpenNextShard()
    {
        while (++_shardIdx < _shards.Length)
        {
            BaseTableView.ShardTable? shard = _shards[_shardIdx];
            if (shard is null) continue;

            MmapByteReader reader = new(shard.File.BasePtr, shard.Length);
            Bound table = new(0, shard.Length);
            _enumerator = new SortedTableEnumerator<MmapByteReader, NoOpPin>(in reader, table);
            _active = shard;
            _checkStart = false;
            if (_shardIdx == _startShard)
            {
                if (!_enumerator.TrySeekBlockOf(in reader, table, _startInclusive))
                {
                    // Every key of the start shard is < startInclusive — nothing to serve here.
                    CloseActive();
                    continue;
                }
                _checkStart = true;
            }

            return true;
        }

        return false;
    }

    private void CloseActive()
    {
        if (_active is null) return;
        _enumerator.Dispose();
        _active = null;
    }

    public ReadOnlySpan<byte> CurrentKey => _enumerator.CurrentKey;

    public ReadOnlySpan<byte> CurrentValue
    {
        get
        {
            Bound bound = _enumerator.CurrentValue;
            // Safety: the bound was produced by the enumerator from within the table's mapped region,
            // which stays mapped for the owning view's lifetime.
            return new ReadOnlySpan<byte>(_active!.File.BasePtr + bound.Offset, checked((int)bound.Length));
        }
    }

    public void Dispose() => CloseActive();
}
