// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Init.Steps;
using Nethermind.Runner.Ethereum.Steps;

namespace Nethermind.Hive;

[RunnerStepDependencies(typeof(InitializeNetwork), typeof(RegisterRpcModules), typeof(RegisterPluginRpcModules))]
public class StartRpcHive : StartRpc
{
    public StartRpcHive(INethermindApi api) : base(api)
    {
    }
}
