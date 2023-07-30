// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
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
public class TrieNodePathCache : IPathTrieNodeCache
{
    class NodeVersions : IEnumerable<NodeAtBlock>
    {
        private SortedSet<NodeAtBlock> _nodes;

        public NodeVersions()
        {
            _nodes = new SortedSet<NodeAtBlock>(new NodeAtBlockComparere());
        }

        public void Add(long blockNumber, TrieNode node)
        {
            NodeAtBlock nad = new(blockNumber, node);
            _nodes.Remove(nad);
            _nodes.Add(nad);
        }

        public TrieNode? GetLatestUntil(long blockNumber, bool remove = false)
        {
            if (blockNumber < _nodes.Min.BlockNumber)
                return null;
            NodeAtBlock atLatest = _nodes.GetViewBetween(_nodes.Min, new NodeAtBlock(blockNumber)).Max;

            if (remove && atLatest is not null)
                _nodes.Remove(atLatest);

            return atLatest?.TrieNode;
        }

        public TrieNode? GetLatest()
        {
            return _nodes.Max?.TrieNode;
        }

        public int Count => _nodes.Count;

        public IEnumerator<NodeAtBlock> GetEnumerator()
        {
            return _nodes.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _nodes.GetEnumerator();
        }
    }

    class NodeAtBlock
    {
        public NodeAtBlock(long blockNo) : this(blockNo, null) { }

        public NodeAtBlock(long blockNo, TrieNode? trieNode)
        {
            BlockNumber = blockNo;
            TrieNode = trieNode;
        }
        public long BlockNumber { get; set; }
        public TrieNode? TrieNode { get; set; }
    }

    class NodeAtBlockComparere : IComparer<NodeAtBlock>
    {
        public int Compare(NodeAtBlock? x, NodeAtBlock? y)
        {
            if (x?.BlockNumber is null || y?.BlockNumber is null)
                throw new ArgumentNullException();
            int result = Comparer<long>.Default.Compare(x.BlockNumber, y.BlockNumber);
            return result;
        }
    }

    private ConcurrentDictionary<byte[], NodeVersions> _nodesByPath = new(Bytes.EqualityComparer);
    private ConcurrentDictionary<Keccak, HashSet<long>> _rootHashToBlock = new();
    private readonly ITrieStore _trieStore;
    private readonly int _maxNumberOfBlocks;
    private int _count;
    private readonly ILogger _logger;

    public int MaxNumberOfBlocks { get => _maxNumberOfBlocks; }
    public int Count { get => _count; }

    public TrieNodePathCache(ITrieStore trieStore, int maxNumberOfBlocks, ILogManager? logManager)
    {
        _trieStore = trieStore;
        _maxNumberOfBlocks = maxNumberOfBlocks;
        _logger = logManager?.GetClassLogger<TrieNodePathCache>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    public TrieNode? GetNode(byte[] path, Keccak keccak)
    {
        if (_nodesByPath.TryGetValue(path, out NodeVersions nodeVersions))
        {
            foreach (NodeAtBlock nodeVersion in nodeVersions)
            {
                if (nodeVersion.TrieNode.Keccak == keccak)
                {
                    Pruning.Metrics.LoadedFromCacheNodesCount++;
                    return nodeVersion.TrieNode;
                }
            }
        }
        return null;
    }

    public TrieNode? GetNode(Keccak? rootHash, byte[] path)
    {
        if (_nodesByPath.TryGetValue(path, out NodeVersions nodeVersions))
        {
            if (rootHash is null)
                return nodeVersions.GetLatest();

            if (_rootHashToBlock.TryGetValue(rootHash, out HashSet<long> blocks))
            {
                long blockNo = blocks.Min();

                TrieNode? node = nodeVersions.GetLatestUntil(blockNo);
                if (node is not null)
                    Pruning.Metrics.LoadedFromCacheNodesCount++;

                return node;
            }
        }
        return null;
    }

    public void AddNode(long blockNumber, TrieNode trieNode)
    {
        if (_maxNumberOfBlocks == 0)
            return;

        byte[] path = trieNode.FullPath;

        AddNodeInternal(path, trieNode, blockNumber);
        if (trieNode.IsLeaf && trieNode.PathToNode.Length < 64)
            AddNodeInternal(trieNode.StoreNibblePathPrefix.Concat(trieNode.PathToNode).ToArray(), trieNode, blockNumber);
    }

    private void AddNodeInternal(byte[] pathToNode, TrieNode trieNode, long blockNumber)
    {
        if (_nodesByPath.TryGetValue(pathToNode, out NodeVersions nodeVersions))
        {
            nodeVersions.Add(blockNumber, trieNode);
        }
        else
        {
            nodeVersions = new NodeVersions
            {
                { blockNumber, trieNode }
            };
            _nodesByPath[pathToNode] = nodeVersions;
        }
        Pruning.Metrics.CachedNodesCount = Interlocked.Increment(ref _count);
        if (_logger.IsDebug) _logger.Debug($"Added node for block {blockNumber} | node version for path {pathToNode.ToHexString()} : {nodeVersions.Count} | Paths cached: {_nodesByPath.Count}");
    }

    public void SetRootHashForBlock(long blockNo, Keccak? rootHash)
    {
        if (_maxNumberOfBlocks == 0)
            return;

        rootHash ??= Keccak.EmptyTreeHash;
        if (_rootHashToBlock.TryGetValue(rootHash, out HashSet<long> blocks))
            blocks.Add(blockNo);
        else
            _rootHashToBlock[rootHash] = new HashSet<long>(new[] { blockNo });
    }

    public void PersistUntilBlock(long blockNumber, IBatch? batch = null)
    {
        List<byte[]> removedPaths = new();
        foreach (KeyValuePair<byte[], NodeVersions> nodeVersion in _nodesByPath)
        {
            TrieNode node = nodeVersion.Value.GetLatestUntil(blockNumber, true);
            if (nodeVersion.Value.Count == 0)
                removedPaths.Add(nodeVersion.Key);

            if (node == null || node.IsPersisted)
                continue;
            _trieStore.SaveNodeDirectly(blockNumber, node, batch);
            node.IsPersisted = true;
        }

        foreach (byte[] path in removedPaths)
            _nodesByPath.Remove(path, out _);

        List<Keccak> removedRoots = new();
        foreach (KeyValuePair<Keccak, HashSet<long>> kvp in _rootHashToBlock)
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
    }

    public void AddRemovedPrefix(long blockNumber, ReadOnlySpan<byte> keyPrefix)
    {
        if (_maxNumberOfBlocks == 0)
            return;

        foreach (byte[] path in _nodesByPath.Keys)
        {
            if (path.Length >= keyPrefix.Length && Bytes.AreEqual(path.AsSpan()[0..keyPrefix.Length], keyPrefix))
            {
                TrieNode deletedNode = _nodesByPath[path].GetLatest().CloneNodeForDeletion();
                _nodesByPath[path].Add(blockNumber, deletedNode);
            }
        }
    }

    public bool IsPathCached(ReadOnlySpan<byte> path)
    {
        return _nodesByPath.ContainsKey(path.ToArray());
    }
}
