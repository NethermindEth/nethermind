// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.State;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Core.Test.Modules;

public class PseudoNetworkModule() : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IGossipPolicy>(Policy.FullGossip)

            // TODO: LastNStateRootTracker

            .AddAdvance<ProtocolsManager>(cfg =>
            {
                cfg
                    .As<IProtocolsManager>()
                    .SingleInstance()
                    .OnActivating((m) =>
                    {
                        ProtocolsManager protocolManager = m.Instance;
                        ISyncConfig syncConfig = m.Context.Resolve<ISyncConfig>();
                        IWorldStateManager worldStateManager = m.Context.Resolve<IWorldStateManager>();

                        if (syncConfig.SnapServingEnabled == true)
                        {
                            protocolManager.AddSupportedCapability(new Capability(Protocol.Snap, 1));
                        }


                    });
            })

            // Some config migration
            .AddDecorator<INetworkConfig>((ctx, networkConfig) =>
            {
                ILogManager logManager = ctx.Resolve<ILogManager>();
                if (networkConfig.DiagTracerEnabled)
                {
                    NetworkDiagTracer.IsEnabled = true;
                }
                if (NetworkDiagTracer.IsEnabled)
                {
                    NetworkDiagTracer.Start(logManager);
                }
                int maxPeersCount = networkConfig.ActivePeersMaxCount;
                Network.Metrics.PeerLimit = maxPeersCount;
                return networkConfig;
            })
            ;
    }

}
