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
    private readonly ILogger _logger = logManager.GetClassLogger<BlockhashCache>();
    private readonly ConcurrentDictionary<Hash256AsKey, CacheNode> _blocks = new();
    private readonly LruCache<Hash256AsKey, Hash256[]> _flatCache = new(32, nameof(BlockhashCache));
    private readonly Lock _lock = new();
    public const ulong MaxDepth = BlockhashProvider.MaxDepth;
    private ulong _minBlock = ulong.MaxValue;
    private Task _pruningTask = Task.CompletedTask;

    public Hash256? GetHash(BlockHeader headBlock, ulong depth) =>
        depth == 0 ? headBlock.Hash
        : depth == 1 ? headBlock.ParentHash
        : depth > MaxDepth ? null
        : _flatCache.TryGet(headBlock.ParentHash!, out Hash256[] array) ? array[(int)depth - 2]
        : Load(headBlock, depth, out _)?.Hash;

    private CacheNode? Load(BlockHeader blockHeader, ulong depth, out Hash256[]? hashes, CancellationToken cancellationToken = default)
    {
        hashes = null;
        if (depth > MaxDepth) return null;
        bool alwaysAdd = depth == MaxDepth;
        using ArrayPoolListRef<(CacheNode Node, bool NeedToAdd)> blocks = new((int)depth + 1);
        Hash256 currentHash = blockHeader.Hash!;
        CacheNode? currentNode = null;
        bool needToAddAny = false;
        ulong skipped = 0;
        for (ulong i = 0; i <= depth && !cancellationToken.IsCancellationRequested; i++)
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
                    needToAddAny |= needToAdd = currentHeader.Hash is not null;
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

        int ancestorCount = blocks.Count - 1;
        if (ancestorCount == FlatCacheLength(blockHeader))
        {
            hashes = new Hash256[ancestorCount];
            for (int i = 1; i < blocks.Count; i++)
            {
                hashes[i - 1] = blocks[i].Node.Hash;
            }

            if (blockHeader.Hash is not null)
            {
                _flatCache.Set(blockHeader.Hash, hashes);
            }
        }

        int index = (int)depth - (int)skipped;
        return index < 0 ? currentNode // if index <0 then we skipped everything and got it from cache
            : blocks.Count > index
                ? blocks[index].Node
                : null;
    }

    private static int FlatCacheLength(BlockHeader blockHeader) => (int)Math.Min(MaxDepth, blockHeader.Number);

    public Task<Hash256[]?> Prefetch(BlockHeader blockHeader, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        Hash256[]? hashes = null;
        try
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Hash256? blockHash = blockHeader.Hash;

                if (blockHash is null || !_flatCache.TryGet(blockHash, out hashes))
                {
                    if (blockHeader.ParentHash is not null && _flatCache.TryGet(blockHeader.ParentHash, out Hash256[] parentHashes))
                    {
                        int length = FlatCacheLength(blockHeader);
                        hashes = new Hash256[length];
                        hashes[0] = blockHeader.ParentHash;
                        Array.Copy(parentHashes, 0, hashes, 1, length - 1);
                        if (blockHash is not null)
                        {
                            _flatCache.Set(blockHash, hashes);
                        }
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

        bool ShouldPrune() => _minBlock != ulong.MaxValue && _minBlock + MaxDepth * 4 < blockHeader.Number && _pruningTask.IsCompleted;
    }

    public int PruneBefore(ulong blockNumber)
    {
        int removed = 0;
        ulong minBlockNumber = ulong.MaxValue;

        Interlocked.Exchange(ref _minBlock, blockNumber);

        foreach (KeyValuePair<Hash256AsKey, CacheNode> kvp in _blocks)
        {
            if (kvp.Value.Parent?.Number < blockNumber)
            {
                kvp.Value.Parent = null;
            }

            if (kvp.Value.Number < blockNumber)
            {
                if (_blocks.TryRemove(kvp.Key, out CacheNode? node))
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
        // Wait for an in-flight background pruning task (from a prior tenant of the
        // WitnessGeneratingBlockProcessingEnvFactory pool) to complete before clearing, so its
        // PruneBefore doesn't clobber the reset _minBlock after the clear.
        Task pruneTask = _pruningTask;
        if (!pruneTask.IsCompleted)
        {
            try
            {
                pruneTask.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                if (_logger.IsWarn) _logger.Warn($"Background pruning task ended with error during Clear: {e.Message}");
            }
        }

        _blocks.Clear();
        _flatCache.Clear();
        Interlocked.Exchange(ref _minBlock, ulong.MaxValue);
    }

    public void Dispose() => Clear();

    public Stats GetStats()
    {
        Dictionary<CacheNode, int> parents = [];
        int nodes = 0;
        foreach (KeyValuePair<Hash256AsKey, CacheNode> kvp in _blocks)
        {
            CacheNode node = kvp.Value;
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
        public ulong Number { get; } = blockHeader.Number;
        public Hash256 ParentHash { get; } = blockHeader.ParentHash!;
        public CacheNode? Parent { get; set; } = parent;
    }

    public record struct Stats(ulong Nodes, ulong Roots, ulong FlatCache)
    {
        public Stats(int nodes, int roots, int flatCache) : this((ulong)nodes, (ulong)roots, (ulong)flatCache) { }
    }
}
