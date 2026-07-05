// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(LoadGenesisBlock))]
    public class ReviewBlockTree(
        IWorldStateManager worldStateManager,
        IInitConfig initConfig,
        ISyncConfig syncConfig,
        IBlockProcessingQueue blockProcessingQueue,
        IBlockTree blockTree,
        IBlockTreeHealer blockTreeHealer,
        ILogManager logManager
    ) : IStep
    {
        private readonly ILogger _logger = logManager.GetClassLogger<ReviewBlockTree>();

        public Task Execute(CancellationToken cancellationToken)
        {
            HealCanonicalChainIfEnabled();
            return initConfig.ProcessingEnabled
                ? RunBlockTreeInitTasks(cancellationToken)
                : Task.CompletedTask;
        }

        private void HealCanonicalChainIfEnabled()
        {
            if (!initConfig.HealCanonicalChain) return;

            Hash256? startHash = blockTree.Head?.Hash;
            if (startHash is not null)
            {
                if (_logger.IsInfo) _logger.Info($"Healing canonical chain from head {startHash} (depth {initConfig.HealCanonicalChainDepth})...");
                blockTreeHealer.HealCanonicalChain(startHash, initConfig.HealCanonicalChainDepth);
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn("HealCanonicalChain requested but no head block found — skipping.");
            }
        }

        private async Task RunBlockTreeInitTasks(CancellationToken cancellationToken)
        {
            // Full-sync nodes previously used DbBlocksLoader, which throws on a level whose block bytes are
            // gone (e.g. after an invalid-block deletion cascade) and leaves the phantom level in place,
            // where it satisfies IsKnownBlock and deadlocks beacon header sync. The fixer deletes such
            // corrupt levels instead.
            using StartupBlockTreeFixer fixer = new(syncConfig, blockTree, worldStateManager.GlobalStateReader, logManager);
            await blockTree.Accept(fixer, cancellationToken).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Fixing gaps in DB failed.", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsWarn) _logger.Warn("Fixing gaps in DB canceled.");
                }
            });

            blockProcessingQueue.ProcessingQueueEmpty += OnProcessingQueueEmpty;
            if (!blockProcessingQueue.IsEmpty) // Just in case the queue got empty before we subscribed
            {
                await _blocksProcessedTaskSource.Task.WaitAsync(cancellationToken);
            }
            blockProcessingQueue.ProcessingQueueEmpty -= OnProcessingQueueEmpty;
        }

        private readonly TaskCompletionSource _blocksProcessedTaskSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void OnProcessingQueueEmpty(object? sender, EventArgs e) => _blocksProcessedTaskSource.SetResult();
    }
}
