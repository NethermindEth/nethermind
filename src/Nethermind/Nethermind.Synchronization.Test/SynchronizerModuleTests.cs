// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Network.Config;
using Nethermind.State;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class SynchronizerModuleTests
{
    [Test]
    public void SyncPeerPool_should_use_INetworkConfig_MaxActivePeers()
    {
        NetworkConfig networkConfig = new() { MaxActivePeers = 75 };

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(networkConfig))
            .AddModule(new SynchronizerModule(new TestSyncConfig()))
            .AddSingleton(Substitute.For<IWorldStateManager>())
            .Build();

        SyncPeerPool pool = container.Resolve<SyncPeerPool>();

        Assert.That(pool.PeerMaxCount, Is.EqualTo(75));
    }
}
