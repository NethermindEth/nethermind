// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.ByPath;
public class FullLeafHistory : ILeafHistoryStrategy
{
    private ConcurrentDictionary<long, ConcurrentDictionary<byte[], TrieNode>> _leafNodesByBlock = new();
    private ConcurrentDictionary<Keccak, HashSet<long>> _rootHashToBlock = new();
    private Tuple<long, Keccak> latestBlock;
    private int _maxNumberOfBlocks;

    public FullLeafHistory(int maxNumberOfBlocks)
    {
        _maxNumberOfBlocks = maxNumberOfBlocks;
    }

    public void Init(ITrieStore trieStore)
    {
    }

    public byte[]? GetLeafNode(byte[] path)
    {
        return latestBlock is null ? null : GetLeafNode(latestBlock.Item2, path);
    }

    public byte[]? GetLeafNode(Keccak rootHash, byte[] path)
    {
        if (_rootHashToBlock.TryGetValue(rootHash, out HashSet<long> blocks))
        {
            long blockNo = blocks.Min();
            if (_leafNodesByBlock.TryGetValue(blockNo, out ConcurrentDictionary<byte[], TrieNode> leafDictionary))
            {
                if (leafDictionary.TryGetValue(path, out TrieNode node))
                    return node.FullRlp;
            }
        }
        return null;
    }

    public void AddLeafNode(long blockNumber, TrieNode trieNode)
    {
        if (_maxNumberOfBlocks == 0)
            return;

        if (!_leafNodesByBlock.TryGetValue(blockNumber, out ConcurrentDictionary<byte[], TrieNode> leafDictionary))
        {
            if (_leafNodesByBlock.Keys.Count >= _maxNumberOfBlocks)
            {
                long minVal = _leafNodesByBlock.Keys.Min();
                _leafNodesByBlock.TryRemove(minVal, out _);
            }
            leafDictionary = new(Bytes.EqualityComparer);
            _leafNodesByBlock[blockNumber] = leafDictionary;
        }
        leafDictionary?.TryAdd(trieNode.FullPath, trieNode);
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
