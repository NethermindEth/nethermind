// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Init.Modules;
using Nethermind.Merge.Plugin;

namespace Nethermind.BeaconChain;

/// <summary>
/// Embedded consensus-layer driver: follows the Ethereum beacon chain and drives block
/// processing through the engine API handlers, removing the need for an external
/// consensus client.
/// </summary>
public class BeaconChainPlugin(IBeaconChainConfig config) : INethermindPlugin
{
    public string Name => "BeaconChain";
    public string Description => "Embedded Ethereum consensus-layer driver";
    public string Author => "Nethermind";
    public bool Enabled => config.Enabled;

    public IModule Module => new BeaconChainModule();
}

public class BeaconChainModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<BeaconChainService>()
            // Derived from the execution layer's chain id, never a separate config knob: the two
            // sides must never be able to disagree about which chain they follow.
            .AddSingleton<BeaconChainSpec, ISpecProvider>(specProvider => BeaconChainSpec.ForChainId(specProvider.ChainId))
            .AddSingleton<BeaconChainStore>()
            .AddSingleton<PubkeyCache>()
            .AddSingleton<CheckpointSync>()
            .AddSingleton<BeaconChainStatusHolder>()
            .Bind<IBeaconChainStatusSource, BeaconChainStatusHolder>()
            .AddSingleton<LocalMetadataSource>()
            .AddSingleton<SlotClock>()
            .AddSingleton<BeaconP2P>()
            .AddSingleton<BeaconDiscovery>()
            .AddSingleton<GossipRouter>()
            .AddSingleton<PeerManager>()
            .Bind<IBeaconSyncPeerPool, PeerManager>()
            .AddSingleton<RangeSync>()
            .AddSingleton<BeaconSyncOrchestrator>()
            .AddSingleton<IBlockImporterFactory, BlockImporterFactory>()
            .AddSingleton<ExternalClDetector>()
            .AddSingleton<EngineDriver>()
            .Bind<INewPayloadNotifier, EngineDriver>()
            .Bind<IEngineDriver, EngineDriver>()
            .AddDecorator<IEngineRpcModule, ExternalClInterceptingEngineRpcModule>()
            .AddColumnDatabase<BeaconChainDbColumns>("beaconChain")
            .AddStep(typeof(StartBeaconChain))
            ;
    }
}
