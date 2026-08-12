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

    [Test]
    public void Resolve_syncing_withholds_snap2_without_bal_heal_substitute()
    {
        // BAL healing isn't implemented yet, so this always withholds snap/2 regardless of chain spec.
        using SnapP2PCapabilityResolver resolver = CreateResolver(snapSync: true);
        Assert.That(Resolve(resolver).Contains(Snap2), Is.False);
    }

    [TestCase(SyncMode.StateNodes, SyncMode.Full, true, TestName = "State sync finishing fires Changed")]
    [TestCase(SyncMode.Full, SyncMode.StateNodes, true, TestName = "Regressing from Full fires Changed")]
    [TestCase(SyncMode.StateNodes, SyncMode.FastBlocks, false, TestName = "Non-Full to non-Full does not fire")]
    [TestCase(SyncMode.Full, SyncMode.Full | SyncMode.FastBlockAccessLists, false, TestName = "Full to Full does not fire")]
    public void Raises_Changed_only_when_full_sync_completion_flips(SyncMode previous, SyncMode current, bool expectedFired)
    {
        ISyncConfig syncConfig = new SyncConfig();
        ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
        ISpecProvider specProvider = new TestSpecProvider(new ReleaseSpec());
        using SnapP2PCapabilityResolver resolver = new(syncConfig, syncModeSelector, specProvider, LimboLogs.Instance);

        bool changed = false;
        resolver.Changed += () => changed = true;

        syncModeSelector.Changed += Raise.EventWith(new SyncModeChangedEventArgs(previous, current));

        Assert.That(changed, Is.EqualTo(expectedFired));
    }
}
