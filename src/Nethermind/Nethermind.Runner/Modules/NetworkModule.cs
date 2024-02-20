// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Runner.Modules;

public class NetworkModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        // Some dependency issue with INetworkConfig prevented automatic constructor injection here
        builder.Register<ITimerFactory, INetworkConfig, ILogManager, NodeStatsManager>(
                (tf, nc, lm) => new NodeStatsManager(tf, lm, nc.MaxCandidatePeerCount))
            .As<INodeStatsManager>()
            .SingleInstance();

        builder.RegisterType<IPResolver>()
            .AsSelf()
            .As<IIPResolver>()
            .SingleInstance();

        builder.RegisterType<EnodeContainer>()
            .SingleInstance();

        builder.Register<EnodeContainer, IEnode>(container => container.Enode);
        builder.Register<INethermindApi, ISyncPeerPool>(api => api.SyncPeerPool);
        builder.Register<INethermindApi, ISyncModeSelector>(api => api.SyncModeSelector);
    }
}
