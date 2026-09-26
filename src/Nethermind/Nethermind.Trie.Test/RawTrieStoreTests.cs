// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class RawTrieStoreTests
{
    [Test]
    public void Memory_storage_preserves_empty_root_and_deletes_nodes()
    {
        MemoryNodeStorage storage = new();
        ValueHash256 hash = Keccak.Compute("node").ValueHash256;

        byte[] emptyRoot = storage.Get(null, TreePath.Empty, Keccak.EmptyTreeHash.ValueHash256)!;
        Assert.That(emptyRoot, Is.EqualTo(new byte[] { 128 }));
        Assert.That(storage.KeyExists(null, TreePath.Empty, Keccak.EmptyTreeHash.ValueHash256), Is.True);

        emptyRoot[0] = 0;
        Assert.That(storage.Get(null, TreePath.Empty, Keccak.EmptyTreeHash.ValueHash256), Is.EqualTo(new byte[] { 128 }));

        storage.Set(null, TreePath.Empty, hash, [1]);
        Assert.That(storage.KeyExists(null, TreePath.Empty, hash), Is.True);
        storage.Set(null, TreePath.Empty, hash, default);
        Assert.That(storage.KeyExists(null, TreePath.Empty, hash), Is.False);
    }

    [Test]
    public void Test_node_storage_write_batch_deletes_nodes()
    {
        using MemDb db = new();
        TestNodeStorage storage = new(db);
        ValueHash256 hash = Keccak.Compute("node").ValueHash256;

        storage.Set(null, TreePath.Empty, hash, [1]);
        Assert.That(storage.Get(null, TreePath.Empty, hash), Is.EqualTo(new byte[] { 1 }));

        using (INodeStorage.IWriteBatch batch = storage.StartWriteBatch())
        {
            batch.Set(null, TreePath.Empty, hash, default, WriteFlags.None);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage.Get(null, TreePath.Empty, hash), Is.Null);
            Assert.That(storage.KeyExists(null, TreePath.Empty, hash), Is.False);
        }
    }

    [Test]
    public void SmokeTest()
    {
        MemoryNodeStorage storage = new();
        PatriciaTree patriciaTree = new(new RawTrieStore(storage).GetTrieStore(null), LimboLogs.Instance);

        patriciaTree.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray());
        patriciaTree.Set(TestItem.KeccakB.Bytes, TestItem.KeccakB.BytesToArray());
        patriciaTree.Set(TestItem.KeccakC.Bytes, TestItem.KeccakC.BytesToArray());

        patriciaTree.Commit();
        Assert.That(patriciaTree.RootHash, Is.Not.EqualTo(Keccak.EmptyTreeHash));

        Hash256 rootHash = patriciaTree.RootHash;

        // Recreate
        patriciaTree = new PatriciaTree(new RawTrieStore(storage).GetTrieStore(null), LimboLogs.Instance);
        patriciaTree.RootHash = rootHash;

        Assert.That(patriciaTree.Get(TestItem.KeccakA.Bytes).ToArray(), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
        Assert.That(patriciaTree.Get(TestItem.KeccakB.Bytes).ToArray(), Is.EqualTo(TestItem.KeccakB.BytesToArray()));
        Assert.That(patriciaTree.Get(TestItem.KeccakC.Bytes).ToArray(), Is.EqualTo(TestItem.KeccakC.BytesToArray()));
    }
}
