// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.Init.Steps;

namespace Nethermind.BeaconChain.Legacy.Era;

[StepCommand("replay-beacon-era", "Replay consensus-layer era files from genesis, so that the embedded beacon chain starts without checkpoint sync.")]
[RunnerStepDependencies(typeof(StartMonitoring))]
public class RunEraReplay(EraReplayer replayer) : IStep
{
    public Task Execute(CancellationToken cancellationToken) =>
        Task.Factory.StartNew(() => replayer.Run(cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
