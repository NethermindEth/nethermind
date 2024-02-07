// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Trie.Pruning;

public class IndexedLeafHistory
{
    //private ITrieStore _trieStore;

    //private ConcurrentDictionary<Keccak, long> _rootHashToBlock = new();
    //private ConcurrentDictionary<byte[], BPlusTree<long, byte[]>> _indexedHistory = new(Bytes.EqualityComparer);
    //private int _maxNumberOfBlocks;

    //public IndexedLeafHistory(int maxNumberOfBlocks = 128)
    //{
    //    _maxNumberOfBlocks = maxNumberOfBlocks;
    //}

    //public void Init(ITrieStore trieStore)
    //{
    //    _trieStore = trieStore;
    //}

    //public void AddLeafNode(long blockNumber, TrieNode trieNode)
    //{
    //    if (_maxNumberOfBlocks == 0)
    //        return;

    //    byte[] fullPathBytes = Nibbles.ToBytes(trieNode.FullPath);

    //    //TODO - can get rid of additional DB read?
    //    byte[] oldNode = _trieStore[fullPathBytes];

    //    if (!_indexedHistory.TryGetValue(fullPathBytes, out BPlusTree<long, byte[]> history))
    //    {
    //        history = new BPlusTree<long, byte[]>();
    //        history.EnableCount();
    //        _indexedHistory.TryAdd(fullPathBytes, history);
    //    }
    //    history[blockNumber] = oldNode;
    //}

    //public byte[]? GetLeafNode(Keccak rootHash, byte[] path)
    //{
    //    if (_rootHashToBlock.TryGetValue(rootHash, out long blockNo))
    //    {
    //        byte[] fullPathBytes = Nibbles.ToBytes(path);
    //        if (_indexedHistory.TryGetValue(fullPathBytes, out BPlusTree<long, byte[]> history))
    //        {
    //            return history.FirstOrDefault(b => b.Key > blockNo).Value;
    //        }
    //    }
    //    return null;
    //}

    //public void SetRootHashForBlock(long blockNo, Keccak? rootHash)
    //{
    //    rootHash ??= Keccak.EmptyTreeHash;
    //    _rootHashToBlock[rootHash] = blockNo;
    //    long blockToDelete = blockNo - _maxNumberOfBlocks;

    //    List<byte[]> pathsToDelete = new List<byte[]>();
    //    foreach (var nodeHist in _indexedHistory)
    //    {
    //        nodeHist.Value.Remove(blockToDelete);
    //        if (nodeHist.Value.Count == 0)
    //            pathsToDelete.Add(nodeHist.Key);
    //    }
    //    pathsToDelete.ForEach(p => _indexedHistory.Remove(p, out _));

    //    Keccak oldHash = null;
    //    foreach (var hashBlock in _rootHashToBlock)
    //    {
    //        if (hashBlock.Value == blockToDelete)
    //        {
    //            oldHash = hashBlock.Key;
    //            break;
    //        }
    //    }
    //    if (oldHash is not null)
    //        _rootHashToBlock.Remove(oldHash, out _);
    //}
}
