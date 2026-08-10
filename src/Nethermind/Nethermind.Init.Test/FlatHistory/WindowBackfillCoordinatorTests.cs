// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Init.FlatHistory;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Init.Test.FlatHistory;

public class WindowBackfillCoordinatorTests
{
    // Empty pool + a fresh (never-published) HistoryAvailability means TryComputeTarget always returns false
    // before ever touching the peer selector or constructing a PeerFedWindowImporter, so this coordinator never
    // dereferences `pruner` in either test below - passing null is safe, not merely convenient, for both.
    private static WindowBackfillCoordinator CreateCoordinator(IFlatDbConfig config, bool windowingConfigured)
    {
        HistoryAvailability availability = new(new SnapshotableMemDb());
        HistoryRowFormat rowFormat = HistoryRowFormat.Resolve(availability, windowingConfigured);
        ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
        pool.InitializedPeers.Returns([]);
        NHistPeerSelector selector = new(pool);
        NHistImportPeerSink sink = new(pool, selector);

        return new WindowBackfillCoordinator(
            config,
            selector,
            sink,
            Substitute.For<IColumnsDb<FlatDbColumns>>(),
            Substitute.For<IColumnsDb<FlatHistoryColumns>>(),
            null!,
            availability,
            rowFormat,
            LimboLogs.Instance);
    }

    [Test]
    public void Constructor_WhenUnwindowed_DoesNotStart()
    {
        IFlatDbConfig config = new FlatDbConfig { HistoryRetentionBlocks = 0 };

        using WindowBackfillCoordinator coordinator = CreateCoordinator(config, windowingConfigured: false);

        Assert.That(coordinator.Started, Is.False, "HistoryRetentionBlocks = 0 must never start a background backfill attempt");
    }

    [Test]
    public void Constructor_WhenWindowed_Starts()
    {
        IFlatDbConfig config = new FlatDbConfig { HistoryRetentionBlocks = 100 };

        using WindowBackfillCoordinator coordinator = CreateCoordinator(config, windowingConfigured: true);

        Assert.That(coordinator.Started, Is.True, "a windowed (v3) database with HistoryRetentionBlocks > 0 must start the background backfill attempt");
    }
}
