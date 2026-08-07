// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Specs;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
public class SnapP2PCapabilityResolverTests
{
    private static SnapP2PCapabilityResolver CreateResolver(
        bool snapServing = false, bool snapSync = false, SyncMode mode = SyncMode.StateNodes,
        bool balEnabled = true, bool flatEnabled = true, BlockHeader? firstPivotHeader = null)
    {
        ISyncConfig syncConfig = new SyncConfig { SnapServingEnabled = snapServing, SnapSync = snapSync };
        ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
        syncModeSelector.Current.Returns(mode);
        IStateSyncPivot stateSyncPivot = Substitute.For<IStateSyncPivot>();
        stateSyncPivot.FirstPivotHeader.Returns(firstPivotHeader);
        ISpecProvider specProvider = new TestSpecProvider(new ReleaseSpec { IsEip7928Enabled = balEnabled });
        IFlatDbConfig flatDbConfig = new FlatDbConfig { Enabled = flatEnabled };
        return new SnapP2PCapabilityResolver(syncConfig, syncModeSelector, stateSyncPivot, specProvider, flatDbConfig, LimboLogs.Instance);
    }

    private static HashSet<Capability> Resolve(SnapP2PCapabilityResolver resolver)
    {
        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);
        return capabilities;
    }

    private static readonly Capability Snap1 = new(Protocol.Snap, SnapVersions.Snap1);
    private static readonly Capability Snap2 = new(Protocol.Snap, SnapVersions.Snap2);
    private static readonly BlockHeader PivotHeader = Build.A.BlockHeader.TestObject;

    [TestCase(true, false, true, TestName = "Serving advertises snap regardless of sync")]
    [TestCase(false, true, true, TestName = "Snap-syncing advertises snap")]
    [TestCase(false, false, false, TestName = "Neither serving nor snap-syncing")]
    public void Resolve_advertises_snap1(bool snapServing, bool snapSync, bool expected)
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(snapServing, snapSync);
        Assert.That(Resolve(resolver).Contains(Snap1), Is.EqualTo(expected));
    }

    [Test]
    public void Resolve_serving_only_advertises_snap2_from_chain_spec_alone()
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(snapServing: true, flatEnabled: false, firstPivotHeader: null);
        Assert.That(Resolve(resolver).Contains(Snap2), Is.True);
    }

    [TestCase(true, true, true, TestName = "Syncing with flat db, BAL chain and known pivot advertises snap/2")]
    [TestCase(false, true, true, TestName = "Syncing without flat db withholds snap/2")]
    [TestCase(true, false, true, TestName = "Syncing on a non-BAL chain withholds snap/2")]
    public void Resolve_syncing_requires_bal_healing(bool flatEnabled, bool balEnabled, bool hasPivot)
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(snapSync: true, flatEnabled: flatEnabled, balEnabled: balEnabled,
            firstPivotHeader: hasPivot ? PivotHeader : null);
        bool expected = flatEnabled && balEnabled && hasPivot;
        Assert.That(Resolve(resolver).Contains(Snap2), Is.EqualTo(expected));
    }

    [Test]
    public void Resolve_syncing_withholds_snap2_until_first_pivot_is_known()
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(snapSync: true, firstPivotHeader: null);
        Assert.That(Resolve(resolver).Contains(Snap2), Is.False);
    }
}
