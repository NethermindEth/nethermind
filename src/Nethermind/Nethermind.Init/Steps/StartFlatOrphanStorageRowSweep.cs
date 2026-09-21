// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.History;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(dependencies: [typeof(InitializeBlockchain)])]
public class StartFlatOrphanStorageRowSweep(
    FlatStateActivationPolicy activationPolicy,
    IFlatDbConfig config,
    IBlockTree blockTree,
    Lazy<OrphanStorageRowSweep> sweep,
    ILogManager logManager) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<StartFlatOrphanStorageRowSweep>();

    public Task Execute(CancellationToken cancellationToken)
    {
        if (!activationPolicy.ShouldTurnOnFlatDb()) return Task.CompletedTask;

        if (!sweep.Value.Supported)
        {
            if (!sweep.Value.AlreadyHandled)
            {
                sweep.Value.MarkUnsupported();
                if (_logger.IsInfo) _logger.Info("Flat history orphan storage row sweep skipped and recorded: this history is windowed or carries a published floor, and the sweep's judgement is only sound over post-value rows captured contiguously from genesis; the live slots are covered by the flat state sweep.");
            }

            return Task.CompletedTask;
        }

        bool repair = !sweep.Value.AlreadyHandled || config.SweepOrphanStorage;
        if (!repair && !config.VerifyOrphanStorage) return Task.CompletedTask;

        sweep.Value.Start(repair, (ulong)(blockTree.Head?.Number ?? 0));
        return Task.CompletedTask;
    }
}
