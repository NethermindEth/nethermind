// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class RebuildFlatTrie(
    IBlockTree blockTree,
    FlatTrieRebuilder rebuilder,
    IProcessExitSource exitSource,
    IFlatDbConfig flatDbConfig,
    ILogManager logManager
) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<RebuildFlatTrie>();

    public async Task Execute(CancellationToken cancellationToken)
    {
        // Both recovery steps re-point/rewrite the head and exit; running them in one boot has unspecified order.
        // Fail fast and point to the two-boot workflow (rebuild -> read RECOVERED STATE ROOT from logs -> rewrite).
        if (flatDbConfig.RebuildTrieFromLeaves && !string.IsNullOrWhiteSpace(flatDbConfig.RewriteHeadStateRoot))
        {
            if (_logger.IsError) _logger.Error(
                "Cannot set both RebuildTrieFromLeaves and RewriteHeadStateRoot in one boot. Use the two-boot workflow: " +
                "(1) boot with RebuildTrieFromLeaves=true and read the RECOVERED STATE ROOT from the logs, then " +
                "(2) set RewriteHeadStateRoot to that root and reboot.");
            exitSource.Exit(1);
            return;
        }

        if (flatDbConfig.Layout == FlatLayout.PreimageFlat)
        {
            if (_logger.IsError) _logger.Error("Cannot rebuild trie from leaves with FlatLayout.PreimageFlat. Use FlatLayout.Flat or FlatLayout.FlatInTrie instead.");
            exitSource.Exit(1);
            return;
        }

        long targetBlockNumber = flatDbConfig.RebuildTrieTargetBlockNumber;
        if (targetBlockNumber <= 0)
        {
            BlockHeader? head = blockTree.Head?.Header;
            if (head is null)
            {
                if (_logger.IsError) _logger.Error("Cannot rebuild trie from leaves: no head block available and RebuildTrieTargetBlockNumber not set.");
                exitSource.Exit(1);
                return;
            }

            targetBlockNumber = (long)head.Number;
        }

        if (_logger.IsWarn) _logger.Warn($"Rebuilding flat trie nodes from leaves for target block {targetBlockNumber}.");

        try
        {
            Hash256 recoveredRoot = await rebuilder.Rebuild(targetBlockNumber, cancellationToken);
            if (_logger.IsWarn) _logger.Warn($"Flat trie rebuild finished. RECOVERED STATE ROOT: {recoveredRoot} at block {targetBlockNumber}.");
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsInfo) _logger.Info("Flat trie rebuild cancelled by user.");
            exitSource.Exit(1);
            return;
        }

        exitSource.Exit(0);
    }
}
