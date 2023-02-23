// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CSharpTest.Net.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Trie.Pruning;

public class IndexedLeafHistory : ILeafHistoryStrategy
{
    private ITrieStore _trieStore;

    private ConcurrentDictionary<Keccak, long> _rootHashToBlock = new();
    private ConcurrentDictionary<byte[], BPlusTree<long, byte[]>> _indexedHistory = new(Bytes.EqualityComparer);

    public IndexedLeafHistory()
    {
    }

    public void Init(ITrieStore trieStore)
    {
        _trieStore = trieStore;
    }

    public void AddLeafNode(long blockNumber, TrieNode trieNode)
    {
        byte[] fullPathBytes = Nibbles.ToBytes(trieNode.FullPath);

        byte[] oldNode = _trieStore[fullPathBytes];

        if (!_indexedHistory.TryGetValue(fullPathBytes, out BPlusTree<long, byte[]> history))
        {
            history = new BPlusTree<long, byte[]>();
            _indexedHistory.TryAdd(fullPathBytes, history);
        }
        history[blockNumber] = oldNode;
    }

    public byte[]? GetLeafNode(Keccak rootHash, byte[] path)
    {
        if (_rootHashToBlock.TryGetValue(rootHash, out long blockNo))
        {
            byte[] fullPathBytes = Nibbles.ToBytes(path);
            if (_indexedHistory.TryGetValue(fullPathBytes, out BPlusTree<long, byte[]> history))
            {
                return history.FirstOrDefault(b => b.Key > blockNo).Value;
            }
        }
        return null;
    }

    public void SetRootHashForBlock(long blockNo, Keccak? rootHash)
    {
        rootHash ??= Keccak.EmptyTreeHash;
        _rootHashToBlock[rootHash] = blockNo;

        //if (blockNo >= (latestBlock?.Item1 ?? 0))
        //{
        //    latestBlock = new Tuple<long, Keccak>(blockNo, rootHash);
        //}
    }
}

public class BlockHistoryRecord
{
    private long _blockNumber;
    private readonly ConcurrentDictionary<byte[], byte[]> _rawData;

    public BlockHistoryRecord(long blockNumber)
    {
        _blockNumber = blockNumber;
        _rawData= new ConcurrentDictionary<byte[], byte[]>(Bytes.EqualityComparer);
    }

    public void AddOrUpdate(byte[] pathBytes, byte[] rawNodeData)
    {
        _rawData.AddOrUpdate(pathBytes, rawNodeData, (key, oldVal) => rawNodeData);
    }

    public byte[] Get(byte[] path)
    {
        return _rawData.TryGetValue(path, out byte[] nodeData) ? nodeData : null;
    }
}

//public class AccountIndex
//{
//    public byte[] Path { get; internal set; }
//    //private SortedList<long, long> _index;
//    private BPlusTree<long, byte[]> _indexedHistory;

//    public AccountIndex(byte[] path)
//    {
//        Path = path;
//        _indexedHistory = new BPlusTree<long, byte[]>();
//    }

//    public void Add(long blockMarker)
//    {
//        //_index.Add(blockMarker, blockMarker);
//        _indexedHistory.AddOrUpdate()
//    }

//    public long FindLowestLargerThan(long blockNumber)
//    {
//        foreach (var kvp in _index)
//        {
//            if (kvp.Key > blockNumber)
//                return kvp.Value;
//        }
//        return -1;
//    }
//}

