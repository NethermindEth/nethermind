// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class OverlayTrieStoreTests
{
    [Test]
    public void TrieStore_OverlayExistingStore()
    {
        IDbProvider dbProvider = TestMemDbProvider.Init();
        TrieStore existingStore = TestTrieStoreFactory.Build(dbProvider.StateDb, LimboLogs.Instance);

        PatriciaTree patriciaTree = new PatriciaTree(existingStore, LimboLogs.Instance);
        patriciaTree.Set(TestItem.Keccaks[0].Bytes, TestItem.Keccaks[0].BytesToArray());
        patriciaTree.Set(TestItem.Keccaks[1].Bytes, TestItem.Keccaks[1].BytesToArray());
        patriciaTree.Commit();
        Hash256 originalRoot = patriciaTree.RootHash;
        int originalKeyCount = dbProvider.StateDb.GetAllKeys().Count();

        ReadOnlyDbProvider readOnlyDbProvider = dbProvider.AsReadOnly(true);
        ITrieStore overlayStore = new OverlayTrieStore(readOnlyDbProvider.GetDb<IDb>(DbNames.State), existingStore.AsReadOnly());

        // Modify the overlay tree
        PatriciaTree overlayedTree = new PatriciaTree(overlayStore, LimboLogs.Instance);
        overlayedTree.RootHash = originalRoot;
        overlayedTree.Get(TestItem.Keccaks[0].Bytes).ToArray().Should().BeEquivalentTo(TestItem.Keccaks[0].BytesToArray());
        overlayedTree.Get(TestItem.Keccaks[1].Bytes).ToArray().Should().BeEquivalentTo(TestItem.Keccaks[1].BytesToArray());
        overlayedTree.Set(TestItem.Keccaks[2].Bytes, TestItem.Keccaks[2].BytesToArray());
        overlayedTree.Set(TestItem.Keccaks[3].Bytes, TestItem.Keccaks[3].BytesToArray());
        overlayedTree.Commit();
        Hash256 newRoot = overlayedTree.RootHash;

        // Verify that the db is modified
        readOnlyDbProvider.GetDb<IDb>(DbNames.State).GetAllKeys().Count().Should().NotBe(originalKeyCount);

        // It can read the modified db
        overlayedTree = new PatriciaTree(overlayStore, LimboLogs.Instance);
        overlayedTree.RootHash = newRoot;
        overlayedTree.Get(TestItem.Keccaks[0].Bytes).ToArray().Should().BeEquivalentTo(TestItem.Keccaks[0].BytesToArray());
        overlayedTree.Get(TestItem.Keccaks[1].Bytes).ToArray().Should().BeEquivalentTo(TestItem.Keccaks[1].BytesToArray());
        overlayedTree.Get(TestItem.Keccaks[2].Bytes).ToArray().Should().BeEquivalentTo(TestItem.Keccaks[2].BytesToArray());
        overlayedTree.Get(TestItem.Keccaks[3].Bytes).ToArray().Should().BeEquivalentTo(TestItem.Keccaks[3].BytesToArray());

        // Now we clear it
        readOnlyDbProvider.ClearTempChanges();

        // It should throw because the overlayed keys are now missing.
        readOnlyDbProvider.GetDb<IDb>(DbNames.State).GetAllKeys().Count().Should().Be(originalKeyCount);
        overlayedTree = new PatriciaTree(overlayStore, LimboLogs.Instance);
        Action act = () =>
        {
            overlayedTree.RootHash = newRoot;
            overlayedTree.Get(TestItem.Keccaks[0].Bytes).ToArray().Should()
                .BeEquivalentTo(TestItem.Keccaks[0].BytesToArray());
        };
        act.Should().Throw<MissingTrieNodeException>(); // The root is now missing.

        // After all this, the original should not change.
        dbProvider.StateDb.GetAllKeys().Count().Should().Be(originalKeyCount);
    }
}
