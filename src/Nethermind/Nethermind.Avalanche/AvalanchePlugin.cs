// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Avalanche;

/// <summary>
/// Consensus plugin enabling Avalanche C-Chain (EVM) support. Activated when the loaded
/// chainspec declares <c>"engine": { "avalanche": { ... } }</c>.
/// </summary>
/// <remarks>
/// Avalanche's consensus (Snowman) and networking are provided by AvalancheGo. A full node
/// integration runs Nethermind's EVM as the C-Chain VM over AvalancheGo's <c>rpcchainvm</c>
/// gRPC interface. This plugin currently wires the Avalanche spec/fork framework; the
/// rpcchainvm VM server and Coreth-compatible block processing are tracked as follow-up work.
/// </remarks>
public class AvalanchePlugin(ChainSpec chainSpec) : IConsensusPlugin
{
    public string Author => "Nethermind";
    public string Name => "Avalanche";
    public string Description => "Avalanche C-Chain (EVM) support for Nethermind";

    public bool Enabled => chainSpec.SealEngineType == SealEngineType;

    public string SealEngineType => Core.SealEngineType.Avalanche;

    public bool MustInitialize => true;

    public IModule Module => new AvalancheModule(chainSpec);
}

public class AvalancheModule(ChainSpec chainSpec) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton(chainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<AvalancheChainSpecEngineParameters>())
            .AddSingleton<ISpecProvider, AvalancheChainSpecBasedSpecProvider>();
    }
}
