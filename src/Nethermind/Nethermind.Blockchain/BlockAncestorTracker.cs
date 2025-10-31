// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using NonBlocking;
using Prometheus;

namespace Nethermind.Blockchain;

public interface IBlockAncestorTracker
{
    Hash256? GetAncestor(Hash256 hash, long depth);
}

/// <summary>
/// Track ancestor of a block using event from blocktree. It works by keeping a 256*128 mapping of the possible answer
/// populated in background, effectively trading of memory for latency.
/// </summary>
public sealed class BlockAncestorTracker: IBlockAncestorTracker, IDisposable
{
    // Default should consume 1MB of ram.
    public const int DEFAULT_MAX_DEPTH = 256;
    public const int DEFAULT_LAST_N_BLOCK_TO_KEEP = 128;

    // How many block back to keep track of. 256 is the query range of `BlockHashProvider`.
    private readonly int _maxDepth;

    // How many number of block can query for their ancestor. Mainly to limit RAM.
    private readonly int _keptBlocks;

    // What is the N-th ancestor of a hash. The N is the index-1 of the array and the key is the current block.
    // EG: _ancestor[0][block] is the pparent, while _ancestor[1][block] is the grandparent.
    private ConcurrentDictionary<Hash256, Hash256>[] _ancestors;

    // There is potentially a slight delay when ingest channel is published and when it will actually be processed
    // meaning there is a slight time where the next block cannot query. This header is updated sync, and is used as a
    // gap with its parent.
    private BlockHeader? _lastBlockHeader = null;

    // Channel to run ingestion in background.
    private ChannelWriter<BlockHeader>? _ingestChannel;
    private bool _isDisposed = false;

    private Counter _hitMiss = Prometheus.Metrics.CreateCounter("blockhash_populate_hit_miss", "populate_time", "hit");
    private Counter _time = Prometheus.Metrics.CreateCounter("blockhash_populate_time", "populate_time");
    private Counter _count = Prometheus.Metrics.CreateCounter("blockhash_populate_count", "populate_time");
    private readonly IBlockTree _blockTree;

    public BlockAncestorTracker(
        IBlockTree blockTree,
        bool ingestSynchronously = false,
        int maxDepth = DEFAULT_MAX_DEPTH,
        int keptBlocks = DEFAULT_LAST_N_BLOCK_TO_KEEP
        )
    {
        _maxDepth = maxDepth;
        _keptBlocks = keptBlocks;

        _blockTree = blockTree;

        _ancestors = new ConcurrentDictionary<Hash256, Hash256>[_maxDepth];
        for (int i = 0; i < _maxDepth; i++)
        {
            _ancestors[i] = new ConcurrentDictionary<Hash256, Hash256>();
        }

        if (_blockTree.Head is not null)
        {
            for (long blockNumber = (_blockTree.Head?.Number ?? 0) - _keptBlocks; blockNumber <= _blockTree.Head.Number; blockNumber++)
            {
                if (blockNumber <= 0) continue;
                BlockHeader header = _blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.None);
                PopulateBasedOnBlockHeader(header);
            }
        }

        // Dont block block added to main
        if (!ingestSynchronously)
        {
            Channel<BlockHeader> channel = Channel.CreateBounded<BlockHeader>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true
            });
            _ingestChannel = channel.Writer;

            Task.Run(async () =>
            {
                await foreach (BlockHeader blockHeader in channel.Reader.ReadAllAsync())
                {
                    PopulateBasedOnBlockHeader(blockHeader);
                }
            });
        }

        _blockTree.BlockAddedToMain += BlockTreeOnBlockAddedToMain;

    }

    private void BlockTreeOnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
    {
        if (_ingestChannel is not null)
        {
            _lastBlockHeader = e.Block.Header;
            SpinWait sw = new SpinWait();
            while(!_ingestChannel.TryWrite(e.Block.Header))
            {
                if (_isDisposed) break;
                sw.SpinOnce();
            }
        }
        else
        {
            PopulateBasedOnBlockHeader(e.Block.Header);
        }

    }

    private void PopulateBasedOnBlockHeader(BlockHeader header)
    {
        long sw = Stopwatch.GetTimestamp();

        Hash256 currentHash = header.Hash;
        Hash256 parentHash = header.ParentHash;
        if (parentHash is null) return; // genesis
        _ancestors[0].TryAdd(currentHash, parentHash);

        bool shouldBuildManual = false;

        // Forward from parent
        for (int i = 0; i < _maxDepth - 1; i++)
        {
            if (_ancestors[i].TryGetValue(parentHash, out var parentAncestor))
            {
                _ancestors[i + 1].TryAdd(currentHash, parentAncestor);
            }
            else
            {
                shouldBuildManual = true;
                break;
            }
        }

        // Was not populated before, so we re-create everything.
        if (shouldBuildManual)
        {
            BlockHeader? current = _blockTree.FindParentHeader(header, BlockTreeLookupOptions.None);
            for (int i = 0; i < _maxDepth && current is not null; i++)
            {
                _ancestors[i][currentHash] = current.Hash;
                current = _blockTree.FindParentHeader(current, BlockTreeLookupOptions.None);
            }
        }

        // Remove entries for block older than _keeptBlocks
        if (_ancestors[_keptBlocks - 1].TryGetValue(parentHash, out Hash256 toRemove))
        {
            int maxSize = 0;

            foreach (ConcurrentDictionary<Hash256, Hash256> concurrentDictionary in _ancestors)
            {
                concurrentDictionary.TryRemove(toRemove, out Hash256 _);
                maxSize = Math.Max(maxSize, concurrentDictionary.Count);
            }

            // This happen in case of a lot of reorg across the execution of the application.
            // In which case, we clean it up, by tracking the needed ancestors for current,
            // and then removing all other entry.
            if (maxSize > _keptBlocks * 2)
            {
                HashSet<Hash256> toKeep = new HashSet<Hash256>();
                foreach (ConcurrentDictionary<Hash256, Hash256> concurrentDictionary in _ancestors)
                {
                    if (concurrentDictionary.TryGetValue(currentHash, out Hash256? ancestor))
                    {
                        toKeep.Add(ancestor);
                    }
                }

                HashSet<Hash256> toRemoveSet = new HashSet<Hash256>();
                foreach (ConcurrentDictionary<Hash256, Hash256> concurrentDictionary in _ancestors)
                {
                    toRemoveSet.Clear();
                    foreach (var kv in concurrentDictionary)
                    {
                        if (!toKeep.Contains(kv.Key))
                        {
                            toRemoveSet.Add(kv.Key);
                        }
                    }

                    foreach (Hash256 hash256 in toRemoveSet)
                    {
                        concurrentDictionary.TryRemove(hash256, out Hash256 removed);
                    }
                }
            }
        }

        _time.Inc(Stopwatch.GetTimestamp() - sw);
        _count.Inc();
    }

    public Hash256? GetAncestor(Hash256 hash, long depth)
    {
        if (depth >= _maxDepth || depth < 0) return null;

        // Node: lookback == 0 is parent.
        if (_ancestors[depth].TryGetValue(hash, out var hash256))
        {
            _hitMiss.WithLabels("hit").Inc();
            return hash256;
        }

        BlockHeader? lastBlockHeader = _lastBlockHeader;
        if (lastBlockHeader?.Hash == hash)
        {
            if (depth == 0)
            {
                return lastBlockHeader.ParentHash;
            }
            return GetAncestor(lastBlockHeader.ParentHash, depth - 1);
        }

        _hitMiss.WithLabels("miss").Inc();
        return null;
    }

    public void Dispose()
    {
        _blockTree.BlockAddedToMain -= BlockTreeOnBlockAddedToMain;
        _ingestChannel?.TryComplete();
        _isDisposed = true;
    }
}
