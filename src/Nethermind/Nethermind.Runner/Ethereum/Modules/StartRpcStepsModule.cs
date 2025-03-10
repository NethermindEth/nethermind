// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Runner.Ethereum.Steps;

namespace Nethermind.Runner.Ethereum.Modules;

public class StartRpcStepsModule: Module
{
    // These cant be referred in `Nethermind.Init`.
    public static readonly StepInfo[] BuiltInSteps =
    [
        typeof(StartGrpc),
        typeof(StartRpc),
    ];

    protected override void Load(ContainerBuilder builder)
    {
        foreach (var builtInStep in BuiltInSteps)
        {
            builder.AddStep(builtInStep);
        }
    }
}
