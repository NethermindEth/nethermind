// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;

namespace Nethermind.Runner.Ethereum.Steps;

[RunnerCommand("run", IsDefault = true, Description = "Run the Nethermind client")]
[RunnerStepDependencies(
    typeof(LogHardwareInfo),
    typeof(StartMonitoring),
    typeof(StartBlockProducer),
    typeof(EvmWarmer),
    typeof(DatabaseMigrations),
    typeof(StartLogIndex),
    typeof(StartRpc)
)]
public class NethermindClientStep : IStep
{
    public Task Execute(CancellationToken cancellationToken) => Task.CompletedTask;
}
