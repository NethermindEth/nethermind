// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Rewards;
using Nethermind.Core.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.EthereumClassic;

/// <summary>
/// Plugin for Ethereum Classic Etchash consensus.
/// Named "EthereumClassicPlugin" to ensure it loads AFTER EthashPlugin alphabetically,
/// allowing its Autofac registrations to override EthashPlugin's IDifficultyCalculator and IEthash.
/// </summary>
public class EthereumClassicPlugin(ChainSpec chainSpec) : INethermindPlugin
{
    public string Name => "Etchash";
    public string Description => "Ethereum Classic Etchash Consensus (ECIP-1099)";
    public string Author => "Nethermind";

    private EtchashChainSpecEngineParameters? GetEtchashParams() =>
        chainSpec.EngineChainSpecParametersProvider?.AllChainSpecParameters
            .OfType<EtchashChainSpecEngineParameters>().FirstOrDefault();

    public bool Enabled => GetEtchashParams() is not null;

    public Task Init(INethermindApi api)
    {
        // Set gas token ticker to ETC for logging
        BlocksConfig.GasTokenTicker = "ETC";
        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol() => Task.CompletedTask;
    public Task InitRpcModules() => Task.CompletedTask;

    public IModule? Module
    {
        get
        {
            var p = GetEtchashParams();
            if (p is null) return null;

            if (p.Ecip1099Transition is null)
                throw new InvalidOperationException("ecip1099Transition is required for Etchash chains");
            if (p.Ecip1017EraRounds <= 0)
                throw new InvalidOperationException("ecip1017EraRounds is required for Etchash chains");

            return new EthereumClassicModule(
                p.Ecip1099Transition.Value,
                p.Ecip1017EraRounds,
                p.DieHardTransition,
                p.GothamTransition,
                p.Ecip1041Transition);
        }
    }
}

public class EthereumClassicModule(
    long ecip1099Transition,
    long ecip1017EraRounds,
    long? dieHardTransition,
    long? gothamTransition,
    long? ecip1041Transition) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        // Override IEthash with Etchash implementation
        builder.Register(ctx => new Etchash(ctx.Resolve<Logging.ILogManager>(), ecip1099Transition))
            .As<IEthash>()
            .SingleInstance();

        // Override IDifficultyCalculator with ETC-specific implementation
        // Bomb transitions are configurable via chainspec
        builder.Register(ctx => new EtchashDifficultyCalculator(
                ctx.Resolve<ISpecProvider>(),
                dieHardTransition,
                gothamTransition,
                ecip1041Transition))
            .As<IDifficultyCalculator>()
            .SingleInstance();

        // Override IRewardCalculatorSource with ETC-specific implementation
        // Era period is configurable: 5M for mainnet, 2M for Mordor
        builder.Register(_ => new EtcRewardCalculator(ecip1017EraRounds))
            .As<IRewardCalculatorSource>()
            .SingleInstance();
    }
}
