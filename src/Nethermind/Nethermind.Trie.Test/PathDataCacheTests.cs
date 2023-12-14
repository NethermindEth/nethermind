// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.ByPathState;
using Nethermind.Logging;
using Nethermind.Trie.ByPath;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;


public class PathDataCacheTests
{
    private static readonly ILogManager Logger = NUnitLogManager.Instance;

    [Test()]
    public void Get_node_latest_version()
    {
        PathDataCache cache = new(new TrieStoreByPath(new ByPathStateDb(new MemColumnsDb<StateColumns>(), Logger), Logger), Logger);

        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000001234"));
        TrieNode node = CreateResolvedLeaf(path1, 160.ToByteArray(), 60);
        cache.AddNodeData(1, node);
        cache.AddNodeData(1, new TrieNode(NodeType.Branch, path: Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x000000000000000000000000000000000000000000000000000000000000123")), Array.Empty<byte>()));

        cache.CloseContext(10, TestItem.KeccakF);

        NodeData? retrieved = cache.GetNodeDataAtRoot(TestItem.KeccakF, path1);

        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved.RLP, Is.EqualTo(node.FullRlp.ToArray()).Using(Bytes.EqualityComparer));
    }

    [Test()]
    public void Get_node_using_path_and_keccak()
    {
        PathDataCache cache = new(new TrieStoreByPath(new ByPathStateDb(new MemColumnsDb<StateColumns>(), Logger), Logger), Logger);

        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000001234"));
        TrieNode node = CreateResolvedLeaf(path1, 160.ToByteArray(), 60);
        node.ResolveKey(NullTrieNodeResolver.Instance, false);

        Hash256 expectedKeccak = node.Keccak;

        cache.AddNodeData(1, node);
        cache.AddNodeData(1, new TrieNode(NodeType.Branch, path: Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x000000000000000000000000000000000000000000000000000000000000123")), Array.Empty<byte>()));

        NodeData? retrieved = cache.GetNodeData(path1, expectedKeccak);

        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved.RLP, Is.EqualTo(node.FullRlp.ToArray()).Using(Bytes.EqualityComparer));

        cache.AddNodeData(1, new(NodeType.Leaf, path1, TestItem.KeccakB));

        retrieved = cache.GetNodeData(path1, TestItem.KeccakA);
        Assert.That(retrieved, Is.Null);
    }

    [Test()]
    public void Get_node_using_root_hash()
    {
        PathDataCache cache = new(new TrieStoreByPath(new ByPathStateDb(new MemColumnsDb<StateColumns>(), Logger), Logger), Logger);

        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000001234"));

        cache.OpenContext(1, null);
        cache.AddNodeData(1, CreateResolvedLeaf(path1, 160.ToByteArray(), 60));
        cache.CloseContext(1, TestItem.KeccakH);

        cache.OpenContext(2, TestItem.KeccakH);
        cache.AddNodeData(2, CreateResolvedLeaf(path1, 320.ToByteArray(), 60));
        cache.CloseContext(2, TestItem.KeccakG);

        cache.OpenContext(3, TestItem.KeccakG);
        cache.AddNodeData(3, CreateResolvedLeaf(path1, 640.ToByteArray(), 60));
        cache.CloseContext(3, TestItem.KeccakF);

        var retrieved = cache.GetNodeDataAtRoot(TestItem.KeccakH, path1);

        var retrievedTrieNode = retrieved.ToTrieNode(path1);

        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrievedTrieNode.Value.ToArray(), Is.EqualTo(160.ToByteArray()).Using(Bytes.EqualityComparer));
    }

    [Test()]
    public void Add_deleted_prefix_get_node()
    {
        PathDataCache cache = new(new TrieStoreByPath(new ByPathStateDb(new MemColumnsDb<StateColumns>(), Logger), Logger), Logger, 4);

        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1acf000000000000000000000000000000000000000000000000000000091234"));
        byte[] path2 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1acf100000000000000000000000000000000000000000000000000000091234"));
        byte[] path3 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x2acf000000000000000000000000000000000000000000000000000000091234"));
        byte[] prefix = new byte[] { 1, 10, 12, 15 };

        cache.OpenContext(1, null);
        cache.AddNodeData(1, CreateResolvedLeaf(path1, 64000.ToByteArray(), 60));
        cache.CloseContext(1, TestItem.KeccakH);

        cache.OpenContext(2, TestItem.KeccakH);
        cache.AddNodeData(2, CreateResolvedLeaf(path2, 64000.ToByteArray(), 60));
        cache.CloseContext(2, TestItem.KeccakG);

        cache.OpenContext(3, TestItem.KeccakG);
        cache.AddNodeData(3, CreateResolvedLeaf(path3, 64000.ToByteArray(), 60));
        cache.CloseContext(3, TestItem.KeccakF);

        cache.OpenContext(4, TestItem.KeccakF);
        cache.AddRemovedPrefix(4, prefix);
        cache.CloseContext(4, TestItem.KeccakE);

        Assert.That(cache.GetNodeDataAtRoot(null, path1).RLP, Is.Null);
        Assert.That(cache.GetNodeDataAtRoot(null, path2).RLP, Is.Null);
        Assert.That(cache.GetNodeDataAtRoot(null, path3).ToTrieNode(path3).Value.ToArray(), Is.EqualTo(64000.ToByteArray()).Using(Bytes.EqualityComparer));
    }

    [Test()]
    public void Add_deleted_prefix_persist_get_node()
    {
        ITrieStore trieStore = new TrieStoreByPath(new ByPathStateDb(new MemColumnsDb<StateColumns>(), Logger), Logger);
        PathDataCache cache = new(trieStore, Logger, 4);

        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1acf000000000000000000000000000000000000000000000000000000091234"));
        byte[] path2 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1acf100000000000000000000000000000000000000000000000000000091234"));
        byte[] path3 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x2acf000000000000000000000000000000000000000000000000000000091234"));
        byte[] prefix = new byte[] { 1, 10, 12, 15 };

        cache.OpenContext(1, null);
        cache.AddNodeData(1, CreateResolvedLeaf(path1, 64000.ToByteArray(), 60));
        cache.CloseContext(1, TestItem.KeccakH);

        cache.OpenContext(2, TestItem.KeccakH);
        cache.AddNodeData(2, CreateResolvedLeaf(path2, 64000.ToByteArray(), 60));
        cache.CloseContext(2, TestItem.KeccakG);

        cache.OpenContext(3, TestItem.KeccakG);
        cache.AddNodeData(3, CreateResolvedLeaf(path3, 64000.ToByteArray(), 60));
        cache.CloseContext(3, TestItem.KeccakF);

        //persist path1 to DB - to later check it was successfully deleted
        cache.PersistUntilBlock(1, TestItem.KeccakH);

        cache.OpenContext(4, TestItem.KeccakF);
        cache.AddRemovedPrefix(4, prefix);
        cache.CloseContext(4, TestItem.KeccakE);

        cache.PersistUntilBlock(4, TestItem.KeccakE);

        Assert.That(() => trieStore.LoadRlp(path1), Throws.TypeOf<TrieException>());
        Assert.That(() => trieStore.LoadRlp(path2), Throws.TypeOf<TrieException>());
    }

    [Test()]
    public void Add_deleted_prefix_persist_get_node_2()
    {
        PathDataCache cache = new(new TrieStoreByPath(new ByPathStateDb(new MemColumnsDb<StateColumns>(), Logger), Logger), Logger, 4);

        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1acf000000000000000000000000000000000000000000000000000000091234"));
        byte[] path2 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1acf100000000000000000000000000000000000000000000000000000091234"));
        byte[] path3 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x2acf000000000000000000000000000000000000000000000000000000091234"));
        byte[] prefix = new byte[] { 1, 10, 12, 15 };

        cache.OpenContext(1, null);
        cache.AddNodeData(1, CreateResolvedLeaf(path1, 64000.ToByteArray(), 60));
        cache.CloseContext(1, TestItem.KeccakH);

        cache.OpenContext(2, TestItem.KeccakH);
        cache.AddNodeData(2, CreateResolvedLeaf(path2, 64000.ToByteArray(), 60));
        cache.CloseContext(2, TestItem.KeccakG);

        cache.OpenContext(3, TestItem.KeccakG);
        cache.AddNodeData(3, CreateResolvedLeaf(path3, 64000.ToByteArray(), 60));
        cache.CloseContext(3, TestItem.KeccakF);

        //persist all nodes until this moment
        cache.PersistUntilBlock(3, TestItem.KeccakF);

        //no paths stored in cache when adding deleted prefix
        cache.OpenContext(4, TestItem.KeccakF);
        cache.AddRemovedPrefix(4, prefix);
        cache.CloseContext(4, TestItem.KeccakE);

        NodeData n1 = cache.GetNodeDataAtRoot(null, path1);
        NodeData n2 = cache.GetNodeDataAtRoot(null, path2);

        //should get nodes with null RLP as a marker for deleted data
        //null returned from cache means a miss in data and load from DB
        Assert.That(n1, Is.Not.Null);
        Assert.That(n1.RLP, Is.Null);

        Assert.That(n2, Is.Not.Null);
        Assert.That(n2.RLP, Is.Null);

        //add a node again under a prefix that has been marked as deleted
        cache.OpenContext(5, TestItem.KeccakE);
        cache.AddNodeData(5, CreateResolvedLeaf(path1, 64000.ToByteArray(), 60));
        cache.CloseContext(5, TestItem.KeccakD);

        Assert.That(cache.GetNodeDataAtRoot(null, path1).RLP, Is.Not.Null);
    }

    [Test()]
    public void Test_persist_until_block_1()
    {
        ITrieStore trieStore = new TrieStoreByPath(new ByPathStateDb(new MemColumnsDb<StateColumns>(), Logger), Logger);
        PathDataCache cache = new(trieStore, Logger, 4);

        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1ac0000000000000000000000000000000000000000000000000000000091234"));
        byte[] path2 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1ac1000000000000000000000000000000000000000000000000000000091234"));
        byte[] path3 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x2ac0000000000000000000000000000000000000000000000000000000091234"));

        cache.OpenContext(1, null);
        cache.AddNodeData(1, CreateResolvedLeaf(path1, 64000.ToByteArray(), 60));
        cache.CloseContext(1, TestItem.KeccakH);

        cache.OpenContext(2, TestItem.KeccakH);
        cache.AddNodeData(2, CreateResolvedLeaf(path2, 64000.ToByteArray(), 60));
        cache.CloseContext(2, TestItem.KeccakG);

        cache.OpenContext(3, TestItem.KeccakG);
        cache.AddNodeData(3, CreateResolvedLeaf(path3, 64000.ToByteArray(), 60));
        cache.CloseContext(3, TestItem.KeccakF);

        cache.PersistUntilBlock(2, TestItem.KeccakG);

        Assert.That(trieStore.LoadRlp(path1), Is.Not.Null);
        Assert.That(trieStore.LoadRlp(path2), Is.Not.Null);
        Assert.That(() => trieStore.LoadRlp(path3), Throws.TypeOf<TrieException>());
    }

    [Test()]
    public void Test_persist_until_block_2()
    {
        ITrieStore trieStore = new TrieStoreByPath(new ByPathStateDb(new MemColumnsDb<StateColumns>(), Logger), Logger);
        PathDataCache cache = new(trieStore, Logger);

        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1ac0000000000000000000000000000000000000000000000000000000091234"));

        cache.OpenContext(1, null);
        cache.AddNodeData(1, CreateResolvedLeaf(path1, 64000.ToByteArray(), 60));
        cache.CloseContext(1, TestItem.KeccakH);

        cache.OpenContext(2, TestItem.KeccakH);
        cache.AddNodeData(2, CreateResolvedLeaf(path1, 128000.ToByteArray(), 60));
        cache.CloseContext(2, TestItem.KeccakG);

        cache.OpenContext(3, TestItem.KeccakG);
        cache.AddNodeData(3, CreateResolvedLeaf(path1, 256000.ToByteArray(), 60));
        cache.CloseContext(3, TestItem.KeccakF);

        cache.PersistUntilBlock(2, TestItem.KeccakG);

        byte[]? rlp = trieStore.LoadRlp(path1);
        Assert.That(rlp, Is.Not.Null);

        TrieNode n = new(NodeType.Unknown, rlp);
        n.ResolveNode(trieStore);

        Assert.That(n.Value.ToArray(), Is.EqualTo(128000.ToByteArray()).Using<byte[]>(Bytes.Comparer));
    }

    [Test()]
    public void Test_persist_until_block_3()
    {
        PathDataCache cache = new(new TrieStoreByPath(new ByPathStateDb(new MemColumnsDb<StateColumns>(), Logger), Logger), Logger, 4);

        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1ac0000000000000000000000000000000000000000000000000000000091234"));

        cache.OpenContext(1, null);
        cache.AddNodeData(1, CreateResolvedLeaf(path1, 64000.ToByteArray(), 60));
        cache.CloseContext(1, TestItem.KeccakA);

        cache.OpenContext(2, TestItem.KeccakA);
        cache.AddNodeData(2, CreateResolvedLeaf(path1, 128000.ToByteArray(), 60));
        cache.CloseContext(2, TestItem.KeccakB);

        cache.OpenContext(3, TestItem.KeccakB);
        cache.AddNodeData(3, CreateResolvedLeaf(path1, 256000.ToByteArray(), 60));
        cache.CloseContext(3, TestItem.KeccakC);

        cache.PersistUntilBlock(2, TestItem.KeccakB);

        //check node is not present in cache for blocks 1 & 2
        Assert.That(cache.GetNodeDataAtRoot(TestItem.KeccakA, path1), Is.Null);
        Assert.That(cache.GetNodeDataAtRoot(TestItem.KeccakB, path1), Is.Null);
        //node for block 3 should still be in cache
        Assert.That(cache.GetNodeDataAtRoot(TestItem.KeccakC, path1), Is.Not.Null);
    }

    private TrieNode CreateResolvedLeaf(byte[] path, byte[] value, int keyLength)
    {
        TrieNode trieNode = TrieNodeFactory.CreateLeaf(path.Slice(keyLength), value, path.Slice(0, keyLength), Array.Empty<byte>());
        trieNode.ResolveKey(NullTrieNodeResolver.Instance, false);
        return trieNode;
    }
}
