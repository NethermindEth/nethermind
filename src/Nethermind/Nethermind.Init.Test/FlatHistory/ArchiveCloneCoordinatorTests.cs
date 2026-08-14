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

public class ArchiveCloneCoordinatorTests
{
    // An empty peer pool means TryGetEligibleCloneSource always returns false before this coordinator ever
    // constructs an ArchiveCloneImporter, so `pruner` is never dereferenced in either test below.
    private static ArchiveCloneCoordinator CreateCoordinator(IFlatDbConfig config)
    {
        HistoryAvailability availability = new(new SnapshotableMemDb());
        HistoryRowFormat rowFormat = HistoryRowFormat.Resolve(availability, config.HistoryRetentionBlocks > 0);
        ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
        pool.InitializedPeers.Returns([]);
        NHistPeerSelector selector = new(pool);

        return new ArchiveCloneCoordinator(
            config,
            selector,
            Substitute.For<IColumnsDb<FlatHistoryColumns>>(),
            Substitute.For<IDb>(),
            Substitute.For<IDb>(),
            null!,
            availability,
            rowFormat,
            new ArchiveCloneVerifier(availability, Substitute.For<ICloneHeaderSource>(), LimboLogs.Instance),
            LimboLogs.Instance);
    }

    [Test]
    public void Constructor_WhenCloneDisabled_DoesNotStart()
    {
        IFlatDbConfig config = new FlatDbConfig { HistoryArchiveCloneEnabled = false };

        using ArchiveCloneCoordinator coordinator = CreateCoordinator(config);

        Assert.That(coordinator.Started, Is.False, "Flat.HistoryArchiveCloneEnabled = false must never start a background clone attempt");
    }

    [Test]
    public void Constructor_WhenCloneEnabled_Starts()
    {
        IFlatDbConfig config = new FlatDbConfig { HistoryArchiveCloneEnabled = true };

        using ArchiveCloneCoordinator coordinator = CreateCoordinator(config);

        Assert.That(coordinator.Started, Is.True, "Flat.HistoryArchiveCloneEnabled = true must start the background clone attempt");
    }

    [Test]
    public void Constructor_WhenCloneEnabledOnAWindowedNode_RefusesToStart()
    {
        IFlatDbConfig config = new FlatDbConfig { HistoryArchiveCloneEnabled = true, HistoryRetentionBlocks = 432000 };

        Assert.That(() => CreateCoordinator(config), Throws.InstanceOf<Core.Exceptions.InvalidConfigurationException>(),
            "a full-archive clone target cannot also be a windowed node - both nhist consumers on one peer would breach the per-peer in-flight limit, and a windowed node must never claim full coverage");
    }
}
