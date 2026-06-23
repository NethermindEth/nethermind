// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
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
        using SnapP2PCapabilityResolver resolver = new(syncConfig, syncModeSelector, LimboLogs.Instance);

        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);

        Assert.That(capabilities.Contains(new Capability(Protocol.Snap, 1)), Is.EqualTo(expected));
    }

    [TestCase(false, true, SyncMode.StateNodes, SyncMode.Full, true, TestName = "Snap sync finishing flips snap and fires Changed")]
    [TestCase(false, true, SyncMode.StateNodes, SyncMode.FastBlocks, false, TestName = "Non-Full to non-Full leaves snap unchanged")]
    [TestCase(true, true, SyncMode.StateNodes, SyncMode.Full, false, TestName = "Snap serving keeps snap constant across sync modes")]
    [TestCase(false, false, SyncMode.StateNodes, SyncMode.Full, false, TestName = "No snap sync means no snap to toggle")]
    public void Raises_Changed_only_when_snap_contribution_flips(bool snapServing, bool snapSync, SyncMode previous, SyncMode current, bool expectedFired)
    {
        ISyncConfig syncConfig = new SyncConfig { SnapServingEnabled = snapServing, SnapSync = snapSync };
        ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
        using SnapP2PCapabilityResolver resolver = new(syncConfig, syncModeSelector, LimboLogs.Instance);

        bool changed = false;
        resolver.Changed += () => changed = true;

        syncModeSelector.Changed += Raise.EventWith(new SyncModeChangedEventArgs(previous, current));

        Assert.That(changed, Is.EqualTo(expectedFired));
    }
}
