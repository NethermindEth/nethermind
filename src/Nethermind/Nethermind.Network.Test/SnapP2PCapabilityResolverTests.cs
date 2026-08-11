// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Specs;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
public class SnapP2PCapabilityResolverTests
{
    private static SnapP2PCapabilityResolver CreateResolver(
        bool snapServing = false, bool snapSync = false, SyncMode mode = SyncMode.StateNodes, bool balEnabled = true)
    {
        ISyncConfig syncConfig = new SyncConfig { SnapServingEnabled = snapServing, SnapSync = snapSync };
        ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
        syncModeSelector.Current.Returns(mode);
        ISpecProvider specProvider = new TestSpecProvider(new ReleaseSpec { IsEip7928Enabled = balEnabled });
        return new SnapP2PCapabilityResolver(syncConfig, syncModeSelector, specProvider, LimboLogs.Instance);
    }

    private static HashSet<Capability> Resolve(SnapP2PCapabilityResolver resolver)
    {
        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);
        return capabilities;
    }

    private static readonly Capability Snap1 = new(Protocol.Snap, SnapVersions.Snap1);
    private static readonly Capability Snap2 = new(Protocol.Snap, SnapVersions.Snap2);

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
        using SnapP2PCapabilityResolver resolver = CreateResolver(snapServing: true, mode: SyncMode.Full);
        Assert.That(Resolve(resolver).Contains(Snap2), Is.True);
    }

    [Test]
    public void Resolve_serving_withholds_snap2_while_own_state_sync_is_unfinished()
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(snapServing: true, mode: SyncMode.StateNodes);
        Assert.That(Resolve(resolver).Contains(Snap2), Is.False);
    }

    [TestCase(true, TestName = "Snap-syncing on a BAL chain still withholds snap/2 (no BAL-heal substitute yet)")]
    [TestCase(false, TestName = "Snap-syncing on a non-BAL chain withholds snap/2")]
    public void Resolve_syncing_withholds_snap2_without_bal_heal_substitute(bool balEnabled)
    {
        using SnapP2PCapabilityResolver resolver = CreateResolver(snapSync: true, balEnabled: balEnabled);
        Assert.That(Resolve(resolver).Contains(Snap2), Is.False);
    }
}
