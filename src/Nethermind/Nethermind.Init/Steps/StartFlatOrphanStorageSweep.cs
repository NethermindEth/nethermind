// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(dependencies: [typeof(InitializeBlockchain)])]
public class StartFlatOrphanStorageSweep(
    FlatStateActivationPolicy activationPolicy,
    IFlatDbConfig config,
    IBlockTree blockTree,
    Lazy<OrphanStorageSweep> sweep,
    ILogManager logManager) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<StartFlatOrphanStorageSweep>();

    public Task Execute(CancellationToken cancellationToken)
    {
        if (!activationPolicy.ShouldTurnOnFlatDb()) return Task.CompletedTask;

        if (config.Layout != FlatLayout.Flat)
        {
            if (!sweep.Value.AlreadyHandled)
            {
                sweep.Value.MarkLayoutUnsupported();
                if (_logger.IsInfo) _logger.Info($"Flat orphan storage sweep does not support the {config.Layout} layout; recorded and skipped.");
            }

            return Task.CompletedTask;
        }

        bool repair = !sweep.Value.AlreadyHandled || config.SweepOrphanStorage;
        if (!repair && !config.VerifyOrphanStorage) return Task.CompletedTask;

        sweep.Value.Start(repair, (ulong)(blockTree.Head?.Number ?? 0));
        return Task.CompletedTask;
    }
}
