// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.State;
using Nethermind.State.SnapServer;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Modules;

public class PsudoNetworkModule() : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IFullStateFinder, FullStateFinder>()
            .AddSingleton<IIPResolver, IPResolver>()
            .AddSingleton<IBeaconSyncStrategy>(No.BeaconSync)
            .AddSingleton<IPoSSwitcher>(NoPoS.Instance)

            .AddSingleton<IProtocolValidator, ProtocolValidator>()
            .AddSingleton<IPooledTxsRequestor, PooledTxsRequestor>()
            .AddSingleton<IForkInfo, ForkInfo>()
            .AddSingleton<IGossipPolicy>(Policy.FullGossip)
            .AddComposite<ITxGossipPolicy, CompositeTxGossipPolicy>()

            // TODO: LastNStateRootTracker
            .AddSingleton<ISnapServer, IWorldStateManager>(stateProvider => stateProvider.SnapServer!)

            .AddAdvance<ProtocolsManager>(cfg =>
            {
                cfg
                    .As<IProtocolsManager>()
                    .WithAttributeFiltering()
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

                        if (worldStateManager.HashServer is null)
                        {
                            protocolManager.RemoveSupportedCapability(new Capability(Protocol.NodeData, 1));
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
