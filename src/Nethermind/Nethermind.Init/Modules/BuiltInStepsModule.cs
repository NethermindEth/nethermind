// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;

namespace Nethermind.Init.Modules;

public class BuiltInStepsModule : Module
{
    public static readonly StepInfo[] BuiltInSteps =
    [
        typeof(ApplyMemoryHint),
        typeof(DatabaseMigrations),
        typeof(EraEStep),
        typeof(EraStep),
        typeof(InitializeBlockchain),
        typeof(EvmWarmer),
        typeof(InitializeBlockProducer),
        typeof(InitializeBlockTree),
        typeof(InitializeNetwork),
        typeof(InitializePrecompiles),
        typeof(InitTxTypesAndRlp),
        typeof(LoadGenesisBlock),
        typeof(LogHardwareInfo),
        typeof(RegisterRpcModules),
        typeof(ReviewBlockTree),
        typeof(SetupKeyStore),
        typeof(StartBlockProcessor),
        typeof(StartBlockProducer),
        typeof(StartMonitoring),
        typeof(StartLogIndex)
    ];

    protected override void Load(ContainerBuilder builder)
    {
        foreach (StepInfo builtInStep in BuiltInSteps)
        {
            builder.AddStep(builtInStep);
        }
    }
}
