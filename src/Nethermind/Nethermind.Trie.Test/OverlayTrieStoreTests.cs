// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
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
        TestRawTrieStore existingStore = new TestRawTrieStore(dbProvider.StateDb);

        PatriciaTree patriciaTree = new PatriciaTree(existingStore, LimboLogs.Instance);
        {
            using var _ = existingStore.BeginBlockCommit(0);
            patriciaTree.Set(TestItem.Keccaks[0].Bytes, TestItem.Keccaks[0].BytesToArray());
            patriciaTree.Set(TestItem.Keccaks[1].Bytes, TestItem.Keccaks[1].BytesToArray());
            patriciaTree.Commit();
        }
        Hash256 originalRoot = patriciaTree.RootHash;
        int originalKeyCount = dbProvider.StateDb.GetAllKeys().Count();

        ReadOnlyDbProvider readOnlyDbProvider = dbProvider.AsReadOnly(true);
        ITrieStore overlayStore = new OverlayTrieStore(readOnlyDbProvider.GetDb<IDb>(DbNames.State), existingStore.AsReadOnly());

        // Modify the overlay tree
        PatriciaTree overlaidTree = new PatriciaTree(overlayStore, LimboLogs.Instance);
        overlaidTree.RootHash = originalRoot;
        overlaidTree.Get(TestItem.Keccaks[0].Bytes).ToArray().Should().BeEquivalentTo(TestItem.Keccaks[0].BytesToArray());
        overlaidTree.Get(TestItem.Keccaks[1].Bytes).ToArray().Should().BeEquivalentTo(TestItem.Keccaks[1].BytesToArray());
        overlaidTree.Set(TestItem.Keccaks[2].Bytes, TestItem.Keccaks[2].BytesToArray());
        overlaidTree.Set(TestItem.Keccaks[3].Bytes, TestItem.Keccaks[3].BytesToArray());
        overlaidTree.Commit();
        Hash256 newRoot = overlaidTree.RootHash;

        // Verify that the db is modified
        readOnlyDbProvider.GetDb<IDb>(DbNames.State).GetAllKeys().Count().Should().NotBe(originalKeyCount);

        // It can read the modified db
        overlaidTree = new PatriciaTree(overlayStore, LimboLogs.Instance);
        overlaidTree.RootHash = newRoot;
        overlaidTree.Get(TestItem.Keccaks[0].Bytes).ToArray().Should().BeEquivalentTo(TestItem.Keccaks[0].BytesToArray());
        overlaidTree.Get(TestItem.Keccaks[1].Bytes).ToArray().Should().BeEquivalentTo(TestItem.Keccaks[1].BytesToArray());
        overlaidTree.Get(TestItem.Keccaks[2].Bytes).ToArray().Should().BeEquivalentTo(TestItem.Keccaks[2].BytesToArray());
        overlaidTree.Get(TestItem.Keccaks[3].Bytes).ToArray().Should().BeEquivalentTo(TestItem.Keccaks[3].BytesToArray());

        // Now we clear it
        readOnlyDbProvider.ClearTempChanges();

        // It should throw because the overlaid keys are now missing.
        readOnlyDbProvider.GetDb<IDb>(DbNames.State).GetAllKeys().Count().Should().Be(originalKeyCount);
        overlaidTree = new PatriciaTree(overlayStore, LimboLogs.Instance);
        Action act = () =>
        {
            overlaidTree.RootHash = newRoot;
            overlaidTree.Get(TestItem.Keccaks[0].Bytes).ToArray().Should()
                .BeEquivalentTo(TestItem.Keccaks[0].BytesToArray());
        };
        act.Should().Throw<MissingTrieNodeException>(); // The root is now missing.

        // After all this, the original should not change.
        dbProvider.StateDb.GetAllKeys().Count().Should().Be(originalKeyCount);
    }
}
