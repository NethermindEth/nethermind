// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.ByPath;
public class TrieNodePathCache
{
    class NodeVersions : ConcurrentDictionary<long, TrieNode>
    {
        public NodeVersions() : base() { }
    }

    private ConcurrentDictionary<byte[], NodeVersions> _nodesByPath = new(Bytes.EqualityComparer);
    private ConcurrentDictionary<Keccak, HashSet<long>> _rootHashToBlock = new();
    private Tuple<long, Keccak> latestBlock;
    private readonly int _maxNumberOfBlocks;
    private int _count;
    private readonly ILogger _logger;

    public int MaxNumberOfBlocks { get => _maxNumberOfBlocks; }

    public TrieNodePathCache(ITrieStore trieStore, int maxNumberOfBlocks, ILogManager? logManager)
    {
        _maxNumberOfBlocks = maxNumberOfBlocks;
        _logger = logManager?.GetClassLogger<TrieNodePathCache>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    public TrieNode? GetNode(byte[] path, Keccak keccak)
    {
        if (_nodesByPath.TryGetValue(path, out NodeVersions nodeVersions))
        {
            foreach (KeyValuePair<long, TrieNode> nodeVersion in nodeVersions)
            {
                if (nodeVersion.Value.Keccak == keccak)
                {
                    Pruning.Metrics.LoadedFromCacheNodesCount++;
                    return nodeVersion.Value;
                }
            }
        }
        return null;
    }

    public TrieNode? GetNode(Keccak rootHash, byte[] path)
    {
        if (_nodesByPath.TryGetValue(path, out NodeVersions nodeVersions))
        {
            if (_rootHashToBlock.TryGetValue(rootHash, out HashSet<long> blocks))
            {
                long blockNo = blocks.Min();

                if (!nodeVersions.TryGetValue(blockNo, out TrieNode node))
                {
                    node = new TrieNode(NodeType.Unknown, path);
                    nodeVersions.TryAdd(blockNo, node);
                }
                else
                {
                    Pruning.Metrics.LoadedFromCacheNodesCount++;
                }
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
        if (trieNode.IsLeaf && (trieNode.Key.Length < 64 || trieNode.PathToNode.Length == 0))
            AddNodeInternal(trieNode.PathToNode, trieNode, blockNumber);

    }

    private void AddNodeInternal(byte[] pathToNode, TrieNode trieNode, long blockNumber)
    {
        if (_nodesByPath.TryGetValue(pathToNode, out NodeVersions nodeVersions))
        {
            if (nodeVersions.Keys.Count >= _maxNumberOfBlocks)
            {
                long minVal = nodeVersions.Keys.Min();
                nodeVersions.TryRemove(minVal, out _);

                Pruning.Metrics.CachedNodesCount = Interlocked.Decrement(ref _count);
                _logger.Info($"Block {minVal} removed from cache");
            }
            nodeVersions[blockNumber] = trieNode;
        }
        else
        {
            nodeVersions = new NodeVersions();
            nodeVersions.TryAdd(blockNumber, trieNode);
            _nodesByPath[pathToNode] = nodeVersions;
        }
        Pruning.Metrics.CachedNodesCount = Interlocked.Increment(ref _count);
        _logger.Info($"Added node for block {blockNumber} | node version for path {pathToNode.ToHexString()} : {nodeVersions.Count} | Paths cached: {_nodesByPath.Count}");
    }

    public void SetRootHashForBlock(long blockNo, Keccak? rootHash)
    {
        rootHash ??= Keccak.EmptyTreeHash;
        if (_rootHashToBlock.TryGetValue(rootHash, out HashSet<long> blocks))
            blocks.Add(blockNo);
        else
            _rootHashToBlock[rootHash] = new HashSet<long>(new[] { blockNo });

        if (blockNo >= (latestBlock?.Item1 ?? 0))
        {
            latestBlock = new Tuple<long, Keccak>(blockNo, rootHash);
        }
    }
}
