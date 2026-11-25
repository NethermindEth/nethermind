// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Init.Steps;
using Nethermind.Network;
using Nethermind.Network.Rlpx;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Init.Test;

[TestFixture]
public class InitializeNetworkTests
{
    [Test]
    public async Task Execute_respects_network_and_peer_startup_configuration(
        [Values] bool peerManagerEnabled,
        [Values] bool networkingEnabled,
        [Values] bool synchronizationEnabled)
    {
        IPeerPool peerPool = Substitute.For<IPeerPool>();
        IPeerManager peerManager = Substitute.For<IPeerManager>();
        ISessionMonitor sessionMonitor = Substitute.For<ISessionMonitor>();
        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        ISynchronizer synchronizer = Substitute.For<ISynchronizer>();

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(
                new InitConfig { PeerManagerEnabled = peerManagerEnabled, DiscoveryEnabled = false },
                new SyncConfig
                {
                    NetworkingEnabled = networkingEnabled,
                    SynchronizationEnabled = synchronizationEnabled,
                    SnapServingEnabled = true,
                }))
            .AddSingleton(Substitute.For<IRlpxHost>())
            .AddSingleton(Substitute.For<IStaticNodesManager>())
            .AddSingleton(Substitute.For<ITrustedNodesManager>())
            .AddSingleton(peerPool)
            .AddSingleton(peerManager)
            .AddSingleton(sessionMonitor)
            .AddSingleton(syncPeerPool)
            .AddSingleton(synchronizer)
            .Build();

        InitializeNetwork network = container.Resolve<InitializeNetwork>();

        await network.Execute(CancellationToken.None);

        if (networkingEnabled)
            syncPeerPool.Received(1).Start();
        else
            syncPeerPool.DidNotReceive().Start();

        if (networkingEnabled && synchronizationEnabled)
            synchronizer.Received(1).Start();
        else
            synchronizer.DidNotReceive().Start();

        if (peerManagerEnabled)
        {
            peerPool.Received(1).Start();
            peerManager.Received(1).Start();
            sessionMonitor.Received(1).Start();
        }
        else
        {
            peerPool.DidNotReceive().Start();
            peerManager.DidNotReceive().Start();
            sessionMonitor.DidNotReceive().Start();
        }
    }
}
