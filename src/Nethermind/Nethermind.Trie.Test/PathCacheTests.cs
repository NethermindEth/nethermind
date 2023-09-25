// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.ByPath;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;


public class PathCacheTests
{
    private static readonly ILogManager Logger = NUnitLogManager.Instance;
    private static readonly ITrieStore _trieStore = new TrieStoreByPath(new MemColumnsDb<StateColumns>(), Logger);
    private static readonly int CacheSize = 5;

    [TestCaseSource(typeof(Instances))]
    public void Get_node_latest_version(IPathTrieNodeCache cache)
    {
        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000001234"));
        TrieNode node = new(NodeType.Leaf, path1, TestItem.KeccakA);
        cache.AddNode(1, node);
        cache.AddNode(1, new TrieNode(NodeType.Branch, path: Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x000000000000000000000000000000000000000000000000000000000000123")), Array.Empty<byte>()));

        TrieNode retrieved = cache.GetNodeFromRoot(null, path1);

        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved, Is.EqualTo(node));
    }

    [TestCaseSource(typeof(Instances))]
    public void Get_node_using_path_and_keccak(IPathTrieNodeCache cache)
    {
        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000001234"));
        TrieNode node = new(NodeType.Leaf, path1, TestItem.KeccakA);
        cache.AddNode(1, node);
        cache.AddNode(1, new TrieNode(NodeType.Branch, path: Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x000000000000000000000000000000000000000000000000000000000000123")), Array.Empty<byte>()));

        TrieNode retrieved = cache.GetNode(path1, TestItem.KeccakA);

        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved, Is.EqualTo(node));

        cache.AddNode(1, new(NodeType.Leaf, path1, TestItem.KeccakB));

        retrieved = cache.GetNode(path1, TestItem.KeccakA);
        Assert.That(retrieved, Is.Null);
    }

    [TestCaseSource(typeof(Instances))]
    public void Get_node_using_root_hash(IPathTrieNodeCache cache)
    {
        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000001234"));

        TrieNode node = new(NodeType.Leaf, path1, TestItem.KeccakA);
        cache.AddNode(1, node);
        cache.AddNode(2, new(NodeType.Leaf, path1, TestItem.KeccakB));
        cache.AddNode(3, new(NodeType.Leaf, path1, TestItem.KeccakC));
        cache.SetRootHashForBlock(1, TestItem.KeccakH);
        cache.SetRootHashForBlock(2, TestItem.KeccakG);
        cache.SetRootHashForBlock(3, TestItem.KeccakF);

        TrieNode retrieved = cache.GetNodeFromRoot(TestItem.KeccakH, path1);

        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved, Is.EqualTo(node));
    }

    [TestCaseSource(typeof(Instances))]
    public void Add_deleted_prefix_get_node(IPathTrieNodeCache cache)
    {
        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1acf000000000000000000000000000000000000000000000000000000091234"));
        byte[] path2 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1acf100000000000000000000000000000000000000000000000000000091234"));
        byte[] path3 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x2acf000000000000000000000000000000000000000000000000000000091234"));
        byte[] prefix = new byte[] { 1, 10, 12, 15 };

        cache.PrefixLength = 4;

        cache.AddNode(1, CreateResolvedLeaf(path1, 64000.ToByteArray(), 60));
        cache.AddNode(2, CreateResolvedLeaf(path2, 64000.ToByteArray(), 60));
        cache.AddNode(3, CreateResolvedLeaf(path3, 64000.ToByteArray(), 60));

        cache.SetRootHashForBlock(1, TestItem.KeccakH);
        cache.SetRootHashForBlock(2, TestItem.KeccakG);
        cache.SetRootHashForBlock(3, TestItem.KeccakF);

        cache.AddRemovedPrefix(4, prefix);

        Assert.That(cache.GetNodeFromRoot(null, path1)?.FullRlp, Is.Null);
        Assert.That(cache.GetNodeFromRoot(null, path2)?.FullRlp, Is.Null);
        Assert.That(cache.GetNodeFromRoot(null, path3).Value.ToArray(), Is.Empty);
    }

    [TestCaseSource(typeof(Instances))]
    public void Add_deleted_prefix_persist_get_node(IPathTrieNodeCache cache)
    {
        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1acf000000000000000000000000000000000000000000000000000000091234"));
        byte[] path2 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1acf100000000000000000000000000000000000000000000000000000091234"));
        byte[] path3 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x2acf000000000000000000000000000000000000000000000000000000091234"));
        byte[] prefix = new byte[] { 1, 10, 12, 15 };

        cache.PrefixLength = 4;

        cache.AddNode(1, CreateResolvedLeaf(path1, 64000.ToByteArray(), 60));
        cache.AddNode(2, CreateResolvedLeaf(path2, 64000.ToByteArray(), 60));
        cache.AddNode(3, CreateResolvedLeaf(path3, 64000.ToByteArray(), 60));

        cache.SetRootHashForBlock(1, TestItem.KeccakH);
        cache.SetRootHashForBlock(2, TestItem.KeccakG);
        cache.SetRootHashForBlock(3, TestItem.KeccakF);

        //persist path1 to DB - to later check it was successfully deleted
        cache.PersistUntilBlock(1);

        cache.AddRemovedPrefix(4, prefix);

        cache.PersistUntilBlock(4);

        Assert.That(() => _trieStore.LoadRlp(path1), Throws.TypeOf<TrieException>());
        Assert.That(() => _trieStore.LoadRlp(path2), Throws.TypeOf<TrieException>());
    }

    [TestCaseSource(typeof(Instances))]
    public void Add_deleted_prefix_persist_get_node_2(IPathTrieNodeCache cache)
    {
        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1acf000000000000000000000000000000000000000000000000000000091234"));
        byte[] path2 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1acf100000000000000000000000000000000000000000000000000000091234"));
        byte[] path3 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x2acf000000000000000000000000000000000000000000000000000000091234"));
        byte[] prefix = new byte[] { 1, 10, 12, 15 };

        cache.PrefixLength = 4;

        cache.AddNode(1, CreateResolvedLeaf(path1, 64000.ToByteArray(), 60));
        cache.AddNode(2, CreateResolvedLeaf(path2, 64000.ToByteArray(), 60));
        cache.AddNode(3, CreateResolvedLeaf(path3, 64000.ToByteArray(), 60));

        cache.SetRootHashForBlock(1, TestItem.KeccakH);
        cache.SetRootHashForBlock(2, TestItem.KeccakG);
        cache.SetRootHashForBlock(3, TestItem.KeccakF);

        //persist all nodes until this moment
        cache.PersistUntilBlock(3);

        //no paths stored in cache when adding deleted prefix
        cache.AddRemovedPrefix(4, prefix);

        TrieNode n1 = cache.GetNodeFromRoot(null, path1);
        TrieNode n2 = cache.GetNodeFromRoot(null, path2);

        //should get nodes with null RLP as a marker for deleted data
        //null returned from cache means a miss in data and load from DB
        Assert.That(n1, Is.Not.Null);
        Assert.That(n1.FullRlp.IsNull, Is.True);

        Assert.That(n2, Is.Not.Null);
        Assert.That(n2.FullRlp.IsNull, Is.True);

        //add a node again under a prefix that has been marked as deleted
        cache.AddNode(5, CreateResolvedLeaf(path1, 64000.ToByteArray(), 60));

        Assert.That(cache.GetNodeFromRoot(null, path1).Value.IsNull, Is.True);
    }

    [TestCaseSource(typeof(Instances))]
    public void Test_persist_until_block_1(IPathTrieNodeCache cache)
    {
        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1ac0000000000000000000000000000000000000000000000000000000091234"));
        byte[] path2 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1ac1000000000000000000000000000000000000000000000000000000091234"));
        byte[] path3 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x2ac0000000000000000000000000000000000000000000000000000000091234"));

        cache.AddNode(1, CreateResolvedLeaf(path1, 64000.ToByteArray(), 60));
        cache.AddNode(2, CreateResolvedLeaf(path2, 64000.ToByteArray(), 60));
        cache.AddNode(3, CreateResolvedLeaf(path3, 64000.ToByteArray(), 60));

        cache.PersistUntilBlock(2);

        Assert.That(_trieStore.LoadRlp(path1), Is.Not.Null);
        Assert.That(_trieStore.LoadRlp(path2), Is.Not.Null);
        Assert.That(() => _trieStore.LoadRlp(path3), Throws.TypeOf<TrieException>());
    }

    [TestCaseSource(typeof(Instances))]
    public void Test_persist_until_block_2(IPathTrieNodeCache cache)
    {
        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1ac0000000000000000000000000000000000000000000000000000000091234"));

        cache.AddNode(1, CreateResolvedLeaf(path1, 64000.ToByteArray(), 60));
        cache.AddNode(2, CreateResolvedLeaf(path1, 128000.ToByteArray(), 60));
        cache.AddNode(3, CreateResolvedLeaf(path1, 256000.ToByteArray(), 60));

        cache.PersistUntilBlock(2);

        byte[]? rlp = _trieStore.LoadRlp(path1);
        Assert.That(rlp, Is.Not.Null);

        TrieNode n = new(NodeType.Unknown, rlp);
        n.ResolveNode(_trieStore);

        Assert.That(n.Value.ToArray(), Is.EqualTo(128000.ToByteArray()).Using<byte[]>(Bytes.Comparer));
    }

    [TestCaseSource(typeof(Instances))]
    public void Test_persist_until_block_3(IPathTrieNodeCache cache)
    {
        byte[] path1 = Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x1ac0000000000000000000000000000000000000000000000000000000091234"));

        cache.AddNode(1, CreateResolvedLeaf(path1, 64000.ToByteArray(), 60));
        cache.AddNode(2, CreateResolvedLeaf(path1, 128000.ToByteArray(), 60));
        cache.AddNode(3, CreateResolvedLeaf(path1, 256000.ToByteArray(), 60));

        cache.SetRootHashForBlock(1, TestItem.KeccakA);
        cache.SetRootHashForBlock(2, TestItem.KeccakB);
        cache.SetRootHashForBlock(3, TestItem.KeccakC);

        cache.PersistUntilBlock(2);

        //check node is not present in cache for blocks 1 & 2
        Assert.That(cache.GetNodeFromRoot(TestItem.KeccakA, path1), Is.Null);
        Assert.That(cache.GetNodeFromRoot(TestItem.KeccakB, path1), Is.Null);
        //node for block 3 should still be in cache
        Assert.That(cache.GetNodeFromRoot(TestItem.KeccakC, path1), Is.Not.Null);
    }

    private TrieNode CreateResolvedLeaf(byte[] path, byte[] value, int keyLength)
    {
        TrieNode trieNode = TrieNodeFactory.CreateLeaf(path.Slice(keyLength), value, path.Slice(0, keyLength), Array.Empty<byte>());
        trieNode.ResolveKey(_trieStore, false);
        return trieNode;
    }

    internal class Instances : IEnumerable
    {
        public static IEnumerable TestCases
        {
            get
            {
                yield return new TestCaseData(new TrieNodeBlockCache(_trieStore, CacheSize, Logger));
                yield return new TestCaseData(new TrieNodePathCache(_trieStore, Logger));
            }
        }

        public IEnumerator GetEnumerator()
        {
            return TestCases.GetEnumerator();
        }
    }
}
