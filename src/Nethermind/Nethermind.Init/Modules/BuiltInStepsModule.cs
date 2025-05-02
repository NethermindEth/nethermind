// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Init;
using Nethermind.Init.Steps;

namespace Nethermind.Runner.Ethereum.Modules;

public class BuiltInStepsModule : Module
{
    public static readonly StepInfo[] BuiltInSteps =
    [
        typeof(InitializeStateDb),
        typeof(ApplyMemoryHint),
        typeof(DatabaseMigrations),
        typeof(EraStep),
        typeof(FilterBootnodes),
        typeof(InitCrypto),
        typeof(InitDatabase),
        typeof(InitializeBlockchain),
        typeof(InitializeBlockProducer),
        typeof(InitializeBlockTree),
        typeof(InitializeNetwork),
        typeof(InitializePlugins),
        typeof(InitializePrecompiles),
        typeof(InitTxTypesAndRlp),
        typeof(LoadGenesisBlock),
        typeof(LogHardwareInfo),
        typeof(MigrateConfigs),
        typeof(RegisterPluginRpcModules),
        typeof(RegisterRpcModules),
        typeof(ResolveIps),
        typeof(ReviewBlockTree),
        typeof(SetupKeyStore),
        typeof(StartBlockProcessor),
        typeof(StartBlockProducer),
        typeof(StartLogProducer),
        typeof(StartMonitoring),
    ];

    protected override void Load(ContainerBuilder builder)
    {
        foreach (var builtInStep in BuiltInSteps)
        {
            builder.AddStep(builtInStep);
        }
    }
}
