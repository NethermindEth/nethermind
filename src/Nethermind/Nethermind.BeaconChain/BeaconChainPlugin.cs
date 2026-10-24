// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Diagnostics;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
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
    internal static void ValidatePreset(BeaconChainSpec spec)
    {
        if (spec.SlotsPerEpoch != Presets.SlotsPerEpoch || spec.SecondsPerSlot != Presets.SecondsPerSlot)
            throw new InvalidConfigurationException("The beacon chain slot timing does not match the selected consensus preset.", ExitCodes.ConflictingConfigurations);
    }

    internal static void ValidateGloasSchedule(BeaconChainSpec spec, ISpecProvider execution)
    {
        if (spec.GloasForkEpoch == ulong.MaxValue)
        {
            if (execution.GetFinalSpec().IsAmsterdamEnabled)
                throw new InvalidConfigurationException("The execution layer schedules Amsterdam but the beacon chain does not schedule Gloas.", ExitCodes.ConflictingConfigurations);
            return;
        }
        ulong timestamp = checked(spec.GenesisTime + spec.GloasForkEpoch * spec.SlotsPerEpoch * spec.SecondsPerSlot);
        if (!execution.GetSpec(long.MaxValue - 1, timestamp).IsAmsterdamEnabled
            || (timestamp > 0 && execution.GetSpec(long.MaxValue - 1, timestamp - 1).IsAmsterdamEnabled))
            throw new InvalidConfigurationException($"Gloas must activate with execution-layer Amsterdam at timestamp {timestamp}; align both fork schedules.", ExitCodes.ConflictingConfigurations);
    }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<BeaconChainService>()
            .AddSingleton<ProcessStallWatchdog>()
            // The network comes from the execution layer's chain id so both sides follow the same chain;
            // only the Gloas schedule can be overridden by config.
            .AddSingleton<BeaconChainSpec, ISpecProvider, IBeaconChainConfig>((specProvider, chainConfig) =>
            {
                BeaconPresetSelection.Initialize(specProvider.ChainId);
                BeaconChainSpec spec = BeaconChainSpec.ForChainId(specProvider.ChainId).WithGloasForkOverride(chainConfig.GloasForkEpoch, chainConfig.GloasForkVersion);
                ValidatePreset(spec);
                ValidateGloasSchedule(spec, specProvider);
                return spec;
            })
            .AddSingleton<BeaconChainStore>()
            .AddSingleton<PubkeyCache>()
            .AddSingleton<CheckpointSync>()
            .AddSingleton<BeaconChainStatusHolder>()
            .Bind<IBeaconChainStatusSource, BeaconChainStatusHolder>()
            .AddSingleton<LocalMetadataSource>()
            .AddSingleton<SlotClock>()
            .AddSingleton<ColumnStoreWriter>()
            .AddSingleton<DataColumnSidecarPool>()
            .AddSingleton<ExecutionPayloadEnvelopePool>()
            .AddSingleton<BeaconP2P>()
            .AddSingleton<BeaconDiscovery>()
            .AddSingleton<GossipRouter>()
            .AddSingleton<GossipMessageValidator>()
            .AddSingleton<ColumnGossipRouter>()
            .AddSingleton<PeerManager>()
            .Bind<IBeaconSyncPeerPool, PeerManager>()
            .AddSingleton<RangeSync>()
            .AddSingleton<ColumnBackfill>()
            .AddSingleton<BeaconSyncOrchestrator>()
            .AddSingleton<ForkChoiceSnapshotHolder>()
            .AddSingleton<ProposerLookaheadHolder>()
            .AddSingleton<FailedBlockRoots>()
            .AddSingleton<IBlockImporterFactory, BlockImporterFactory>()
            .AddSingleton<INodeColumnCustodySource, BeaconDiscovery>(discovery => new DiscoveryNodeCustodySource(discovery))
            .AddSingleton<ExternalClDetector>()
            .AddSingleton<EngineDriver>()
            .Bind<IEngineDriver, EngineDriver>()
            .AddDecorator<IEngineRpcModule, ExternalClInterceptingEngineRpcModule>()
            .AddColumnDatabase<BeaconChainDbColumns>("beaconChain")
            .AddStep(typeof(StartBeaconChain))
            ;

        // Gated separately: the API can be off while the driver runs, but not the reverse.
        builder.RegisterModule(new Api.BeaconApiModule());
    }
}
