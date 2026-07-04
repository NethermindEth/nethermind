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
using Nethermind.Core;
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
        ILogManager logManager,
        IPersistedStateSource? persistedStateSource = null
    ) : IStep
    {
        private readonly ILogger _logger = logManager.GetClassLogger<ReviewBlockTree>();

        public Task Execute(CancellationToken cancellationToken)
        {
            HealCanonicalChainIfEnabled();
            FastForwardHeadToPersistedState();
            return initConfig.ProcessingEnabled
                ? RunBlockTreeInitTasks(cancellationToken)
                : Task.CompletedTask;
        }

        /// <summary>
        /// After an unclean shutdown a state backend that cannot roll back can hold persisted state ahead
        /// of the block tree head, and no state exists for the parents of the gap blocks. When the block
        /// matching the persisted state id is already in the tree, move the head onto it before loading DB
        /// blocks, so processing resumes from a block whose state exists instead of stalling on the gap.
        /// </summary>
        private void FastForwardHeadToPersistedState()
        {
            if (persistedStateSource is null
                || !persistedStateSource.TryGetPersistedState(out ulong persistedNumber, out Hash256? persistedRoot))
            {
                return;
            }

            Block? head = blockTree.Head;
            if (head is null || head.Number >= persistedNumber)
            {
                return;
            }

            ChainLevelInfo? level = blockTree.FindLevel(persistedNumber);
            if (level is null)
            {
                return;
            }

            foreach (BlockInfo blockInfo in level.BlockInfos)
            {
                Block? block = blockTree.FindBlock(blockInfo.BlockHash, BlockTreeLookupOptions.None);
                if (block?.StateRoot != persistedRoot)
                {
                    continue;
                }

                if (blockTree.TryUpdateMainChain(block.Header, wereProcessed: true, forceUpdateHeadBlock: true, block))
                {
                    if (_logger.IsInfo) _logger.Info($"Fast-forwarded head from {head.Number} to {block.ToString(Block.Format.Short)} matching the persisted state.");
                }
                else if (_logger.IsWarn)
                {
                    _logger.Warn($"Could not fast-forward head to persisted state block {block.ToString(Block.Format.Short)}; a branch predecessor is missing.");
                }

                return;
            }

            if (_logger.IsInfo) _logger.Info($"Persisted state {persistedNumber} is ahead of head {head.Number} and has no matching local block; waiting for forward sync.");
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
