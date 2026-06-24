// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
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
    public void IsHistorical_DefaultsToFalse()
    {
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        using ReadOnlySnapshotBundle readOnlyBundle =
            new(FlatTestHelpers.SnapshotList(FlatTestHelpers.MakeSnapshot(_pool)), reader, recordDetailedMetrics: false);

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
            isHistorical: isHistorical);

        return new SnapshotBundle(
            readOnlyBundle,
            Substitute.For<ITrieNodeCache>(),
            _pool,
            ResourcePool.Usage.MainBlockProcessing);
    }
}
