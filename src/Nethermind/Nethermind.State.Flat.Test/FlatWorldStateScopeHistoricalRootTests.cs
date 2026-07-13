// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class FlatWorldStateScopeHistoricalRootTests
{
    private ResourcePool _pool = null!;

    [SetUp]
    public void SetUp() => _pool = new ResourcePool(new FlatDbConfig { CompactSize = 2 });

    [Test]
    public void UpdateRootHash_HistoricalScope_DoesNotThrow_AndRetainsKnownRoot()
    {
        // A historical block has a real (non-empty) state root the scope is constructed with. The backing reader has
        // no trie nodes (throws), so recomputing the root must be skipped and the known root retained.
        Hash256 knownRoot = TestItem.KeccakA;
        StateId currentStateId = new(100, knownRoot);

        using FlatWorldStateScope scope = BuildScope(currentStateId, isHistorical: true);

        // Mutate state via the flat snapshot path (the history-backed read path). The trace re-execution reads/writes
        // values this way; only the post-block root recompute would traverse the (absent) trie.
        scope.Get(TestItem.AddressA);

        Assert.That(() => scope.UpdateRootHash(), Throws.Nothing);
        Assert.That(scope.RootHash, Is.EqualTo(knownRoot));
    }

    [Test]
    public void UpdateRootHash_RecentScope_RecomputesRoot()
    {
        // A scope with no changes over an empty-tree root recomputes back to the empty-tree root, proving the call is
        // dispatched to the trie (not skipped). A non-historical bundle defaults IsHistorical to false.
        StateId currentStateId = new(100, Keccak.EmptyTreeHash);

        using FlatWorldStateScope scope = BuildScope(currentStateId, isHistorical: false);

        Assert.That(() => scope.UpdateRootHash(), Throws.Nothing);
        Assert.That(scope.RootHash, Is.EqualTo(Keccak.EmptyTreeHash));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void IsHistorical_FlowsFromReadOnlyBundleToBundle(bool isHistorical)
    {
        using SnapshotBundle bundle = BuildBundle(isHistorical);

        Assert.That(bundle.IsHistorical, Is.EqualTo(isHistorical));
    }

    [Test]
    public void WriteBatch_HistoricalScope_DoesNotThrow_AndFlatOverlayServesReads()
    {
        // Trace re-execution over a historical block commits each tx's changes through a write batch. That write batch
        // previously bulk-set the state/storage tries, resolving trie nodes via the history-backed reader which throws
        // NotSupportedException. A trie-less scope must skip every trie write/hash while still applying the change to
        // the flat overlay so subsequent txs in the block read the updated value.
        Hash256 knownRoot = TestItem.KeccakA;
        StateId currentStateId = new(100, knownRoot);
        Address address = TestItem.AddressA;
        UInt256 slot = (UInt256)7;
        byte[] slotValue = [0x12, 0x34];
        Account written = new(nonce: 1, balance: 5, storageRoot: Keccak.EmptyTreeHash, codeHash: Keccak.OfAnEmptyString);

        using FlatWorldStateScope scope = BuildScope(currentStateId, isHistorical: true);

        Assert.That(() =>
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1);
            batch.Set(address, written);
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, 1);
            storageBatch.Set(in slot, slotValue);
        }, Throws.Nothing);

        Account? readBack = scope.Get(address);
        byte[] slotReadBack = scope.CreateStorageTree(address).Get(in slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(readBack, Is.EqualTo(written));
            Assert.That(slotReadBack, Is.EqualTo(slotValue));
            Assert.That(() => scope.UpdateRootHash(), Throws.Nothing);
            Assert.That(scope.RootHash, Is.EqualTo(knownRoot));
        }
    }

    [Test]
    public void WriteBatch_RecentScope_WritesAndHashesTrie()
    {
        // A non-trie-less scope over the empty-tree root must still bulk-write the state/storage tries and recompute
        // the root, proving the guards only suppress trie work for trie-less scopes.
        StateId currentStateId = new(100, Keccak.EmptyTreeHash);
        Address address = TestItem.AddressB;
        UInt256 slot = (UInt256)3;
        byte[] slotValue = [0xab];
        Account written = new(nonce: 2, balance: 9, storageRoot: Keccak.EmptyTreeHash, codeHash: Keccak.OfAnEmptyString);

        using FlatWorldStateScope scope = BuildScope(currentStateId, isHistorical: false);

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(address, written);
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(address, 1);
            storageBatch.Set(in slot, slotValue);
        }

        scope.UpdateRootHash();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.Get(address), Is.Not.Null);
            Assert.That(scope.CreateStorageTree(address).Get(in slot), Is.EqualTo(slotValue));
            Assert.That(scope.RootHash, Is.Not.EqualTo(Keccak.EmptyTreeHash));
        }
    }

    [Test]
    public void IsHistorical_DefaultsToFalse()
    {
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        using ReadOnlySnapshotBundle readOnlyBundle =
            new(FlatTestHelpers.SnapshotList(FlatTestHelpers.MakeSnapshot(_pool)), reader, recordDetailedMetrics: false, PersistedSnapshotStack.Empty());

        Assert.That(readOnlyBundle.IsHistorical, Is.False);
    }

    private FlatWorldStateScope BuildScope(StateId currentStateId, bool isHistorical)
    {
        SnapshotBundle bundle = BuildBundle(isHistorical);

        return new FlatWorldStateScope(
            currentStateId,
            bundle,
            new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(new MemDb()),
            Substitute.For<IFlatCommitTarget>(),
            new FlatDbConfig { CompactSize = 2 },
            new NoopTrieWarmer(),
            LimboLogs.Instance);
    }

    private SnapshotBundle BuildBundle(bool isHistorical)
    {
        // A history-backed reader throws on every trie-node access, mirroring HistoryBackedPersistenceReader; the
        // historical scope must never call into it during post-block root recompute.
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>())
            .Returns(_ => throw new NotSupportedException());

        ReadOnlySnapshotBundle readOnlyBundle = new(
            FlatTestHelpers.SnapshotList(FlatTestHelpers.MakeSnapshot(_pool)),
            reader,
            recordDetailedMetrics: false,
            PersistedSnapshotStack.Empty(),
            isHistorical: isHistorical);

        return new SnapshotBundle(
            readOnlyBundle,
            Substitute.For<ITrieNodeCache>(),
            _pool,
            ResourcePool.Usage.MainBlockProcessing);
    }
}
