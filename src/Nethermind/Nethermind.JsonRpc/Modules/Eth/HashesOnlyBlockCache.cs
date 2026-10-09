// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>Blocks read for hashes-only responses, bounded by their estimated size rather than their count.</summary>
/// <remarks>
/// Reads are lock-free. Adds, which happen only on a miss, take one lock and evict in insertion order, giving each block
/// read since its last chance one more, at most once per add so readers cannot keep an add evicting.
/// </remarks>
internal sealed class HashesOnlyBlockCache(long byteBudget)
{
    public const long DefaultByteBudget = 32 * 1024 * 1024;

    private readonly ConcurrentDictionary<ValueHash256, HashesOnlyBlock> _blocks = new();
    private readonly Queue<ValueHash256> _insertionOrder = new();
    private readonly Lock _lock = new();
    private long _bytes;
    private long _generation;

    public HashesOnlyBlockCache() : this(DefaultByteBudget)
    {
    }

    /// <summary>The estimated size of the cached blocks.</summary>
    public long Bytes => Volatile.Read(ref _bytes);

    /// <summary>Changes on every <see cref="Clear"/>; read it before reading a block, and pass it to <see cref="Add"/>.</summary>
    public long Generation => Volatile.Read(ref _generation);

    public bool TryGet(in ValueHash256 blockHash, out HashesOnlyBlock? block)
    {
        if (!_blocks.TryGetValue(blockHash, out block))
        {
            return false;
        }

        block.MarkRead();
        return true;
    }

    /// <summary>Adds <paramref name="block"/>, replacing a block cached under the same hash, unless it is larger than the
    /// whole budget or was read before a <see cref="Clear"/> that <paramref name="generation"/> predates.</summary>
    /// <remarks>A replaced block keeps its place in the eviction order.</remarks>
    public void Add(in ValueHash256 blockHash, HashesOnlyBlock block, long generation)
    {
        if (block.EstimatedSize > byteBudget)
        {
            return;
        }

        lock (_lock)
        {
            if (generation != _generation)
            {
                return;
            }

            long bytes = _bytes + block.EstimatedSize;
            if (_blocks.TryGetValue(blockHash, out HashesOnlyBlock? replaced))
            {
                _blocks[blockHash] = block;
                bytes -= replaced.EstimatedSize;
            }
            else
            {
                _blocks[blockHash] = block;
                _insertionOrder.Enqueue(blockHash);
            }

            int secondChances = _insertionOrder.Count;
            while (bytes > byteBudget)
            {
                ValueHash256 oldest = _insertionOrder.Dequeue();
                HashesOnlyBlock candidate = _blocks[oldest];
                if (candidate.TakeRead() && secondChances-- > 0)
                {
                    _insertionOrder.Enqueue(oldest);
                    continue;
                }

                _blocks.TryRemove(oldest, out _);
                bytes -= candidate.EstimatedSize;
            }

            Volatile.Write(ref _bytes, bytes);
        }
    }

    /// <summary>Drops every block, and keeps out any block read before the call.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _blocks.Clear();
            _insertionOrder.Clear();
            Volatile.Write(ref _bytes, 0);
            Volatile.Write(ref _generation, _generation + 1);
        }
    }
}
