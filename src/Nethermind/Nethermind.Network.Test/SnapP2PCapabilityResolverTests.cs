// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
public class SnapP2PCapabilityResolverTests
{
    [TestCase(true, false, SyncMode.StateNodes, true, TestName = "Serving advertises snap regardless of sync")]
    [TestCase(false, true, SyncMode.StateNodes, true, TestName = "Snap-syncing state advertises snap")]
    [TestCase(false, true, SyncMode.Full, false, TestName = "Snap sync finished drops snap")]
    [TestCase(false, false, SyncMode.StateNodes, false, TestName = "Neither serving nor snap-syncing")]
    public void Resolve_advertises_snap_when_serving_or_syncing_state(bool snapServing, bool snapSync, SyncMode currentMode, bool expected)
    {
        ISyncConfig syncConfig = new SyncConfig { SnapServingEnabled = snapServing, SnapSync = snapSync };
        ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
        syncModeSelector.Current.Returns(currentMode);
        using SnapP2PCapabilityResolver resolver = new(syncConfig, syncModeSelector);

        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);

        Assert.That(capabilities.Contains(new Capability(Protocol.Snap, 1)), Is.EqualTo(expected));
    }

    [Test]
    public void Raises_Changed_on_sync_mode_change()
    {
        ISyncConfig syncConfig = new SyncConfig();
        ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
        using SnapP2PCapabilityResolver resolver = new(syncConfig, syncModeSelector);

        bool changed = false;
        resolver.Changed += () => changed = true;

        syncModeSelector.Changed += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.StateNodes, SyncMode.Full));

        Assert.That(changed, Is.True);
    }
}
