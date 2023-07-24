// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.ByPath;
public class TrieNodeBlockCache : IPathTrieNodeCache
{
    public class NodesByBlock : ConcurrentDictionary<long, ConcurrentDictionary<byte[], TrieNode>>
    {
        public NodesByBlock() : base() { }

        private int _nodesCount;
        public int NodesCount { get => _nodesCount; }
        public long MemoryUsed { get; private set; }

        public void AddOrUpdate(long blockNumber, TrieNode trieNode)
        {
            if (!TryGetValue(blockNumber, out ConcurrentDictionary<byte[], TrieNode> nodeDictionary))
            {
                nodeDictionary = new(Bytes.EqualityComparer);
                this[blockNumber] = nodeDictionary;
            }

            TrieNode AddFunc(byte[] key)
            {
                Interlocked.Increment(ref _nodesCount);
                MemoryUsed += trieNode.GetMemorySize(false);
                return trieNode;
            }

            TrieNode UpdateFunc(byte[] key, TrieNode prev)
            {
                MemoryUsed += prev.GetMemorySize(false) - trieNode.GetMemorySize(false);
                return trieNode;
            }

            nodeDictionary.AddOrUpdate(trieNode.FullPath, AddFunc, UpdateFunc);
            // TODO: this causes issues when writing to db - this causes double writes
            if (trieNode.IsLeaf)
                nodeDictionary.AddOrUpdate(trieNode.StoreNibblePathPrefix.Concat(trieNode.PathToNode).ToArray(), AddFunc, UpdateFunc);
        }
    }


    private readonly ITrieStore _trieStore;
    private readonly NodesByBlock _nodesByBlock = new();
    private readonly ConcurrentDictionary<Keccak, HashSet<long>> _rootHashToBlock = new();
    private long _highestBlockNumberCached = -1;
    private int _maxNumberOfBlocks;
    private int _count;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, List<byte[]>> _removedPrefixes;

    public int MaxNumberOfBlocks { get => _maxNumberOfBlocks; }
    public int Count { get => _count; }

    public TrieNodeBlockCache(ITrieStore trieStore, int maxNumberOfBlocks, ILogManager? logManager)
    {
        _trieStore = trieStore;
        _maxNumberOfBlocks = maxNumberOfBlocks;
        _removedPrefixes = new ConcurrentDictionary<long, List<byte[]>> { };
        _logger = logManager?.GetClassLogger<TrieNodeBlockCache>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    public TrieNode? GetNode(byte[] path, Keccak keccak)
    {
        foreach (long blockNumber in _nodesByBlock.Keys.OrderByDescending(b => b))
        {
            if (_removedPrefixes.TryGetValue(blockNumber, out List<byte[]> prefixes))
            {
                foreach (byte[] prefix in prefixes)
                {
                    if (path.Length >= prefix.Length &&
                        Bytes.AreEqual(path.AsSpan()[0..prefix.Length], prefix))
                    {
                        return null;
                    }
                }
            }

            ConcurrentDictionary<byte[], TrieNode> nodeDictionary = _nodesByBlock[blockNumber];
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

    public TrieNode? GetNode(Keccak? rootHash, byte[] path)
    {
        if (_nodesByBlock.Count == 0)
            return null;

        long minBlockNumberStored = _nodesByBlock.Keys.Min();
        long blockNo = _nodesByBlock.Keys.Max();

        if (rootHash is not null && _rootHashToBlock.TryGetValue(rootHash, out HashSet<long> blocks))
        {
            if (_nodesByBlock.Count > 0)
                blockNo = blocks.Max();
        }

        while (blockNo >= minBlockNumberStored)
        {
            if (_removedPrefixes.TryGetValue(blockNo, out List<byte[]> prefixes))
            {
                foreach (byte[] prefix in prefixes)
                {
                    if (path.Length >= prefix.Length &&
                        Bytes.AreEqual(path.AsSpan()[0..prefix.Length], prefix))
                    {
                        return null;
                    }
                }
            }

            if (_nodesByBlock.TryGetValue(blockNo, out ConcurrentDictionary<byte[], TrieNode> nodeDictionary))
            {
                if (nodeDictionary.TryGetValue(path, out TrieNode node))
                {
                    Pruning.Metrics.LoadedFromCacheNodesCount++;
                    return node;
                }
            }
            blockNo--;
        }
        return null;
    }

    public void AddNode(long blockNumber, TrieNode trieNode)
    {
        if (_maxNumberOfBlocks == 0)
            return;

        _nodesByBlock.AddOrUpdate(blockNumber, trieNode);

        Pruning.Metrics.CachedNodesCount = Interlocked.Increment(ref _count);

        Interlocked.CompareExchange(ref _highestBlockNumberCached, blockNumber, _highestBlockNumberCached);
    }

    public void SetRootHashForBlock(long blockNo, Keccak? rootHash)
    {
        if (_nodesByBlock.Count == 0)
            return;

        rootHash ??= Keccak.EmptyTreeHash;
        if (_rootHashToBlock.TryGetValue(rootHash, out HashSet<long> blocks))
            blocks.Add(blockNo);
        else
            _rootHashToBlock[rootHash] = new HashSet<long>(new[] { blockNo });
    }

    public void PersistUntilBlock(long blockNumber, IBatch? batch = null)
    {
        if (_nodesByBlock.IsEmpty) return;

        long minHeldBlockNumber = _nodesByBlock.Keys.Min();
        long currentBlockNumber = minHeldBlockNumber;
        while (currentBlockNumber <= blockNumber)
        {
            if (_removedPrefixes.TryRemove(blockNumber, out List<byte[]> prefixes))
            {
                foreach (byte[] keyPrefix in prefixes)
                {
                    (byte[] startKey, byte[] endKey) = TrieStoreByPath.GetDeleteKeyFromNibblePrefix(keyPrefix);
                    _trieStore.DeleteByRange(startKey, endKey);
                }
            }
            if (_nodesByBlock.TryRemove(blockNumber, out ConcurrentDictionary<byte[], TrieNode> nodesByPath))
            {
                IOrderedEnumerable<TrieNode> orderedValues = nodesByPath.Values.OrderBy(tn => tn.FullRlp, Bytes.Comparer);
                foreach (TrieNode? node in orderedValues)
                {
                    _trieStore.SaveNodeDirectly(blockNumber, node, batch);
                    node.IsPersisted = true;
                }
                // Parallel.ForEach(nodesByPath.Values, node =>
                // {
                //     _trieStore.SaveNodeDirectly(blockNumber, node, batch);
                //     node.IsPersisted = true;
                // });
            }
            currentBlockNumber++;
        }
        List<Keccak> removedRoots = new();
        foreach(KeyValuePair<Keccak, HashSet<long>> kvp in _rootHashToBlock)
        {
            kvp.Value.RemoveWhere(blk => blk <= blockNumber);
            if (kvp.Value.Count == 0)
                removedRoots.Add(kvp.Key);
        }
        foreach (Keccak rootHash in removedRoots)
            _rootHashToBlock.Remove(rootHash, out _);
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

    public void AddRemovedPrefix(long blockNumber, ReadOnlySpan<byte> keyPrefix)
    {
        if (_maxNumberOfBlocks == 0)
            return;

        if (!_removedPrefixes.TryGetValue(blockNumber, out List<byte[]> prefixes))
        {
            prefixes = new List<byte[]>();
            _removedPrefixes[blockNumber] = prefixes;
        }

        prefixes.Add(keyPrefix.ToArray());
    }

    public bool IsPathCached(ReadOnlySpan<byte> path)
    {
        byte[] p = path.ToArray();
        foreach (KeyValuePair<long, ConcurrentDictionary<byte[], TrieNode>> nodes in _nodesByBlock)
        {
            if (nodes.Value.ContainsKey(p))
                return true;
        }
        return false;
    }


    /// <summary>
    /// Add inlined nodes to cache, so can be accessed directly using path
    /// MOVED to PatriciaTrie Commit
    /// </summary>
    /// <param name="blockNumber"></param>
    /// <param name="node"></param>
    private void AddInlinedNodes(long blockNumber, TrieNode node)
    {
        //is it possible for other node types?
        if (node.NodeType != NodeType.Branch)
            return;

        for (int i = 0; i < 16; i++)
        {
            TrieNode childNode = node.GetData(i) as TrieNode;
            if (childNode?.NodeType == NodeType.Leaf && childNode?.FullRlp?.Length < 32)
            {
                _nodesByBlock.AddOrUpdate(blockNumber, childNode);
            }
        }
    }
}
