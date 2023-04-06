// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.ByPath;
public class TrieNodeBlockCache : IPathTrieNodeCache
{
    private readonly ITrieStore _trieStore;
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<byte[], TrieNode>> _nodesByBlock = new();
    private readonly ConcurrentDictionary<Keccak, HashSet<long>> _rootHashToBlock = new();
    private long _highestBlockNumberCached = -1;
    private int _maxNumberOfBlocks;
    private int _count;
    private readonly ILogger _logger;

    public int MaxNumberOfBlocks { get => _maxNumberOfBlocks; }
    public int Count { get => _count; }

    public TrieNodeBlockCache(ITrieStore trieStore, int maxNumberOfBlocks, ILogManager? logManager)
    {
        _trieStore = trieStore;
        _maxNumberOfBlocks = maxNumberOfBlocks;
        _logger = logManager?.GetClassLogger<TrieNodePathCache>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    public TrieNode? GetNode(byte[] path, Keccak keccak)
    {
        foreach (long blockNumer in _nodesByBlock.Keys.OrderByDescending(b => b))
        {
            ConcurrentDictionary<byte[], TrieNode> nodeDictionary = _nodesByBlock[blockNumer];
            if (nodeDictionary.TryGetValue(path, out TrieNode node))
            {
                if (node.Keccak == keccak)
                {
                    Pruning.Metrics.LoadedFromCacheNodesCount++;
                    return node;
                }
            }
        }
        return null;
    }

    public TrieNode? GetNode(Keccak rootHash, byte[] path)
    {
        if (_rootHashToBlock.TryGetValue(rootHash, out HashSet<long> blocks))
        {
            long blockNo = blocks.Min();
            long minBlockNumberStored = _nodesByBlock.Keys.Min();

            while (blockNo >= minBlockNumberStored)
            {
                if (_nodesByBlock.TryGetValue(blockNo, out ConcurrentDictionary<byte[], TrieNode> nodeDictrionary))
                {
                    if (nodeDictrionary.TryGetValue(path, out TrieNode node))
                    {
                        Pruning.Metrics.LoadedFromCacheNodesCount++;
                        return node;
                    }
                }
                blockNo--;
            }
        }
        return null;
    }

    public void AddNode(long blockNumber, TrieNode trieNode)
    {
        if (_maxNumberOfBlocks == 0)
            return;

        if (!_nodesByBlock.TryGetValue(blockNumber, out ConcurrentDictionary<byte[], TrieNode> nodeDictrionary))
        {
            nodeDictrionary = new(Bytes.EqualityComparer);
            _nodesByBlock[blockNumber] = nodeDictrionary;
        }
        nodeDictrionary?.TryAdd(trieNode.FullPath, trieNode);
        if (trieNode.PathToNode == Array.Empty<byte>())
            nodeDictrionary?.TryAdd(trieNode.PathToNode, trieNode);

        Interlocked.CompareExchange(ref _highestBlockNumberCached, blockNumber, _highestBlockNumberCached);
    }

    public void SetRootHashForBlock(long blockNo, Keccak? rootHash)
    {
        rootHash ??= Keccak.EmptyTreeHash;
        if (_rootHashToBlock.TryGetValue(rootHash, out HashSet<long> blocks))
            blocks.Add(blockNo);
        else
            _rootHashToBlock[rootHash] = new HashSet<long>(new[] { blockNo });
    }

    public void PersistUntilBlock(long blockNumber)
    {
        if (_nodesByBlock.IsEmpty)
            return;
        long currentBlockNumber = _nodesByBlock.Keys.Min();
        while (currentBlockNumber <= blockNumber)
        {
            if (_nodesByBlock.TryRemove(blockNumber, out ConcurrentDictionary<byte[], TrieNode> nodesByPath))
            {
                Parallel.ForEach(nodesByPath.Values, node =>
                {
                    _trieStore.SaveNodeDirectly(blockNumber, node);
                    node.IsPersisted = true;
                });
            }
            currentBlockNumber++;
        }
    }

    public void Prune()
    {
        while (_nodesByBlock.Count > _maxNumberOfBlocks)
        {
            long blockToRemove = _nodesByBlock.Keys.Min();
            if (_nodesByBlock.TryRemove(blockToRemove, out ConcurrentDictionary<byte[], TrieNode> nodesByPath))
            {
                Pruning.Metrics.CachedNodesCount = Interlocked.Add(ref _count, -nodesByPath.Count);
            }
            if (_logger.IsInfo) _logger.Info($"Block {blockToRemove} removed from cache");
        }
    }
}
