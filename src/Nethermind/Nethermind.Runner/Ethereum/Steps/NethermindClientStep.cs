// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;

namespace Nethermind.Runner.Ethereum.Steps;

[RunnerCommand("run", IsDefault = true, Description = "Run the Nethermind client")]
[RunnerStepDependencies(
    typeof(LogHardwareInfo),     // Standalone: logs CPU info, MustInitialize=false
    typeof(StartMonitoring),     // Standalone: starts Prometheus/Grafana metrics, MustInitialize=false
    typeof(StartBlockProducer),
    typeof(DatabaseMigrations),  // Background runtime migrations that wait for NotSyncing mode; needs full init
    typeof(StartLogIndex),
    typeof(StartRpc)
)]
public class NethermindClientStep : IStep
{
    public Task Execute(CancellationToken cancellationToken) => Task.CompletedTask;
}
