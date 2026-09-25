// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Init;

[StepCommand("verify-trie", "Verify that the full state trie is stored for the current head.")]
[RunnerStepDependencies(typeof(InitializeBlockTree))]
public class RunVerifyTrie(
    IWorldStateManager worldStateManager,
    IBlockTree blockTree,
    ILogManager logManager)
    : IStep
{
    private ILogger _logger = logManager.GetClassLogger<RunVerifyTrie>();

    /// <inheritdoc/>
    /// <exception cref="StepDependencyException">
    /// There is no head to verify, or the state trie is incomplete.
    /// </exception>
    /// <remarks>
    /// Failure is reported by throwing rather than by setting an exit code, so that the runner sees the command
    /// as unsuccessful. Having nothing to verify counts as failure: a caller asking for verification must not
    /// read "no head" as "the state is fine".
    /// </remarks>
    public Task Execute(CancellationToken cancellationToken)
    {
        _logger!.Info("Collecting trie stats and verifying that no nodes are missing...");

        BlockHeader head = blockTree!.Head?.Header
            ?? throw new StepDependencyException("Verify trie failed: the block tree has no head to verify.");

        _logger.Info($"Starting from {head.Number} {head.StateRoot}{Environment.NewLine}");

        if (!worldStateManager.VerifyTrie(head, cancellationToken))
            throw new StepDependencyException($"Verify trie failed: state at block {head.Number} is incomplete.");

        return Task.CompletedTask;
    }
}
