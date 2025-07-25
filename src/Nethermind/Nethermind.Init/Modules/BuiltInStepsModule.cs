// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;

namespace Nethermind.Runner.Ethereum.Modules;

public class BuiltInStepsModule : Module
{
    public static readonly StepInfo[] BuiltInSteps =
    [
        typeof(ApplyMemoryHint),
        typeof(DatabaseMigrations),
        typeof(EraStep),
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
        typeof(StartMonitoring),
    ];

    protected override void Load(ContainerBuilder builder)
    {
        foreach (StepInfo builtInStep in BuiltInSteps)
        {
            builder.AddStep(builtInStep);
        }
    }
}
