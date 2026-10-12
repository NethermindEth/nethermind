// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class RawTrieStoreTests
{
    [Test]
    public void SmokeTest()
    {
        MemDb db = new();
        PatriciaTree patriciaTree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);

        patriciaTree.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray());
        patriciaTree.Set(TestItem.KeccakB.Bytes, TestItem.KeccakB.BytesToArray());
        patriciaTree.Set(TestItem.KeccakC.Bytes, TestItem.KeccakC.BytesToArray());

        patriciaTree.Commit();
        Assert.That(patriciaTree.RootHash, Is.Not.EqualTo(Keccak.EmptyTreeHash));

        Hash256 rootHash = patriciaTree.RootHash;

        // Recreate
        patriciaTree = new PatriciaTree(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        patriciaTree.RootHash = rootHash;

        Assert.That(patriciaTree.Get(TestItem.KeccakA.Bytes), Is.SequenceEqualTo(TestItem.KeccakA.BytesToArray()));
        Assert.That(patriciaTree.Get(TestItem.KeccakB.Bytes), Is.SequenceEqualTo(TestItem.KeccakB.BytesToArray()));
        Assert.That(patriciaTree.Get(TestItem.KeccakC.Bytes), Is.SequenceEqualTo(TestItem.KeccakC.BytesToArray()));
    }

    // The zkEVM guest's TrieNode.Unseal marks a written node dirty in place, which is sound only while no
    // two lookups share a node.
    [Test]
    public void Every_lookup_gets_its_own_node()
    {
        RawTrieStore store = new(new MemDb());
        IScopedTrieStore scoped = store.GetTrieStore(null);
        TrieNode first = store.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakA);
        TrieNode firstScoped = scoped.FindCachedOrUnknown(TreePath.Empty, TestItem.KeccakA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.FindCachedOrUnknown(null, TreePath.Empty, TestItem.KeccakA), Is.Not.SameAs(first));
            Assert.That(scoped.FindCachedOrUnknown(TreePath.Empty, TestItem.KeccakA), Is.Not.SameAs(firstScoped));
        }
    }
}
