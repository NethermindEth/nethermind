// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Hive;

[RunnerStepDependencies(typeof(InitializeNetwork), Optional = true)]
public class HiveStep(
    HiveRunner hiveRunner,
    IProcessExitSource processExitSource,
    ILogManager logManager
) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<HiveStep>();

    public async Task Execute(CancellationToken cancellationToken)
    {
        if (_logger.IsInfo) _logger.Info("Hive is starting");

        await hiveRunner.Start(processExitSource.Token);
    }
}
