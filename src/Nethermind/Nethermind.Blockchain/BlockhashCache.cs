// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Logging;

namespace Nethermind.Blockchain;

public class BlockhashCache(IHeaderFinder headerFinder, ILogManager logManager) : IDisposable, IBlockhashCache
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly ConcurrentDictionary<Hash256AsKey, CacheNode> _blocks = new();
    private readonly LruCache<Hash256AsKey, Hash256[]> _flatCache = new(32, nameof(BlockhashCache));
    private readonly Lock _lock = new();
    public const int MaxDepth = BlockhashProvider.MaxDepth;
    private long _minBlock = int.MaxValue;
    private Task _pruningTask = Task.CompletedTask;

    public Hash256? GetHash(BlockHeader headBlock, int depth) =>
        depth == 0 ? headBlock.Hash
        : depth == 1 ? headBlock.ParentHash
        : depth > MaxDepth ? null
        : _flatCache.TryGet(headBlock.ParentHash!, out Hash256[] array) ? array[depth - 1]
        : Load(headBlock, depth, out _)?.Hash;

    private CacheNode? Load(BlockHeader blockHeader, int depth, out Hash256[]? hashes, CancellationToken cancellationToken = default)
    {
        hashes = null;
        if (depth > MaxDepth) return null;
        bool alwaysAdd = depth == MaxDepth;
        using ArrayPoolListRef<(CacheNode Node, bool NeedToAdd)> blocks = new(depth + 1);
        Hash256 currentHash = blockHeader.Hash!;
        CacheNode currentNode = null;
        bool needToAddAny = false;
        int skipped = 0;
        for (int i = 0; i <= depth && !cancellationToken.IsCancellationRequested; i++)
        {
            bool needToAdd = false;
            if (currentNode is null)
            {
                if (!_blocks.TryGetValue(currentHash, out currentNode))
                {
                    BlockHeader? currentHeader = i == 0 ? blockHeader : headerFinder.Get(currentHash, blockHeader.Number - i);
                    if (currentHeader is null)
                    {
                        break;
                    }

                    currentNode = new CacheNode(currentHeader);
                    needToAdd = true;
                    needToAddAny = true;
                }
            }

            if (alwaysAdd || blocks.Count != 0 || needToAdd || currentNode.Parent is null)
            {
                blocks.Add((currentNode, needToAdd));
            }
            else
            {
                skipped++;
            }

            if (i != depth)
            {
                currentHash = currentNode.ParentHash;
                currentNode = currentNode.Parent;
            }
        }

        if (needToAddAny && !cancellationToken.IsCancellationRequested)
        {
            (CacheNode Node, bool NeedToAdd) parentNode = blocks[^1];
            InterlockedEx.Min(ref _minBlock, parentNode.Node.Number);
            for (int i = blocks.Count - 2; i >= 0 && !cancellationToken.IsCancellationRequested; i--)
            {
                if (parentNode.NeedToAdd)
                {
                    parentNode.Node = _blocks.GetOrAdd(parentNode.Node.Hash, parentNode.Node);
                }

                (CacheNode Node, bool NeedToAdd) current = blocks[i];
                current.Node.Parent = parentNode.Node;
                parentNode = current;
            }

            if (parentNode.NeedToAdd)
            {
                _blocks.TryAdd(parentNode.Node.Hash, parentNode.Node);
            }
        }

        int ancestorHashCount = blocks.Count - 1;
        if (ancestorHashCount == FlatCacheLength(blockHeader))
        {
            hashes = new Hash256[ancestorHashCount];
            for (int i = 1; i < blocks.Count; i++)
            {
                hashes[i - 1] = blocks[i].Node.Hash;
            }
            _flatCache.Set(blockHeader.Hash, hashes);
        }

        int index = depth - skipped;
        return index < 0 ? currentNode // if index <0 then we skipped everything and got it from cache
            : blocks.Count > index
                ? blocks[index].Node
                : null;
    }

    private static int FlatCacheLength(BlockHeader blockHeader) => (int)Math.Min(MaxDepth, blockHeader.Number);

    public Task<Hash256[]?> Prefetch(BlockHeader blockHeader, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            Hash256[]? hashes = null;
            try
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    if (!_flatCache.TryGet(blockHeader.Hash, out hashes))
                    {
                        if (_flatCache.TryGet(blockHeader.ParentHash, out Hash256[] parentHashes))
                        {
                            int length = FlatCacheLength(blockHeader);
                            hashes = new Hash256[length];
                            hashes[0] = blockHeader.ParentHash;
                            Array.Copy(parentHashes, 0, hashes, 1, Math.Min(length - 1, MaxDepth - 1));
                            _flatCache.Set(blockHeader.Hash, hashes);
                        }
                        else
                        {
                            Load(blockHeader, MaxDepth, out hashes, cancellationToken);
                        }
                    }
                }

                PruneInBackground(blockHeader);
            }
            catch (Exception e)
            {
                if (_logger.IsWarn) _logger.Warn($"Background fetch failed for block {blockHeader.Number}: {e.Message}");
            }

            return hashes;
        });
    }

    private void PruneInBackground(BlockHeader blockHeader)
    {
        if (ShouldPrune())
        {
            lock (_lock)
            {
                if (ShouldPrune())
                {
                    _pruningTask = Task.Run(() =>
                    {
                        try
                        {
                            PruneBefore(blockHeader.Number - MaxDepth * 2);
                        }
                        catch (Exception e)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Background pruning failed for block {blockHeader.Number}: {e.Message}");
                        }
                    });
                }
            }
        }

        bool ShouldPrune() => _minBlock + MaxDepth * 4 < blockHeader.Number && _pruningTask.IsCompleted;
    }

    public int PruneBefore(long blockNumber)
    {
        int removed = 0;
        long minBlockNumber = long.MaxValue;

        Interlocked.Exchange(ref _minBlock, blockNumber);

        foreach (KeyValuePair<Hash256AsKey, CacheNode> kvp in _blocks)
        {
            if (kvp.Value.Parent?.Number < blockNumber)
            {
                kvp.Value.Parent = null;
            }

            if (kvp.Value.Number < blockNumber)
            {
                if (_blocks.TryRemove(kvp.Key, out CacheNode node))
                {
                    _flatCache.Delete(node.Hash);
                    removed++;

                }
            }
            else
            {
                minBlockNumber = Math.Min(minBlockNumber, kvp.Value.Number);
            }
        }

        InterlockedEx.Min(ref _minBlock, minBlockNumber);

        return removed;
    }

    public bool Contains(Hash256 blockHash) => _blocks.ContainsKey(blockHash);

    public void Clear()
    {
        _blocks.Clear();
    }

    public void Dispose()
    {
        Clear();
    }

    public Stats GetStats()
    {
        Dictionary<CacheNode, int> parents = new();
        int nodes = 0;
        foreach (CacheNode node in _blocks.Values)
        {
            parents.GetOrAdd(node, static _ => 0);
            if (node.Parent is not null)
            {
                parents.GetOrAdd(node.Parent, static _ => 0)++;
            }

            nodes++;
        }

        return new Stats(nodes, parents.Values.Count(p => p == 0), _flatCache.Count);
    }

    /// <summary>
    /// Represents a cached block node in a linked-list structure
    /// </summary>
    private class CacheNode(BlockHeader blockHeader, CacheNode? parent = null)
    {
        public Hash256 Hash { get; } = blockHeader.Hash!;
        public long Number { get; } = blockHeader.Number;
        public Hash256 ParentHash { get; } = blockHeader.ParentHash!;
        public CacheNode? Parent { get; set; } = parent;
    }

    public record struct Stats(int Nodes, int Roots, int FlatCache);
}
