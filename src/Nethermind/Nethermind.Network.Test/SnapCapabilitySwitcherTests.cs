// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

public class SnapCapabilitySwitcherTests
{
    [Test]
    public void Dispose_ShouldUnsubscribeFromSyncModeChanges()
    {
        IProtocolsManager protocolsManager = Substitute.For<IProtocolsManager>();
        ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
        SnapCapabilitySwitcher snapCapabilitySwitcher = new(protocolsManager, syncModeSelector, LimboLogs.Instance);

        snapCapabilitySwitcher.EnableSnapCapabilityUntilSynced();
        snapCapabilitySwitcher.Dispose();
        syncModeSelector.Changed += Raise.EventWith(this, new SyncModeChangedEventArgs(SyncMode.SnapSync, SyncMode.Full));

        protocolsManager.Received(1).RemoveSupportedCapability(new Capability(Protocol.Snap, 1));
    }
}
