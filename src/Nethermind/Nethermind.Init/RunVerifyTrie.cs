// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Init;

[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class RunVerifyTrie(
    IWorldStateManager worldStateManager,
    IBlockTree blockTree,
    IProcessExitSource processExitSource,
    ILogManager logManager)
    : IStep
{
    private ILogger _logger = logManager.GetClassLogger<RunVerifyTrie>();

    public Task Execute(CancellationToken cancellationToken)
    {
        _logger!.Info("Collecting trie stats and verifying that no nodes are missing...");
        BlockHeader? head = blockTree!.Head?.Header;
        if (head is not null)
        {
            _logger.Info($"Starting from {head.Number} {head.StateRoot}{Environment.NewLine}");
            worldStateManager.VerifyTrie(head, processExitSource!.Token);
        }

        return Task.CompletedTask;
    }
}
