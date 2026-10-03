// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
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
        ChainSpec? chainSpec = null,
        IReceiptStorage? receiptStorage = null,
        IReceiptConfig? receiptConfig = null
    ) : IStep
    {
        private readonly ILogger _logger = logManager.GetClassLogger<ReviewBlockTree>();

        public Task Execute(CancellationToken cancellationToken)
        {
            ValidateEip8304History();
            HealCanonicalChainIfEnabled();
            return initConfig.ProcessingEnabled
                ? RunBlockTreeInitTasks(cancellationToken)
                : Task.CompletedTask;
        }

        private void ValidateEip8304History()
        {
            if (chainSpec?.Parameters.Eip8304TransitionTimestamp is null)
                return;

            if (receiptConfig is not null && !receiptConfig.StoreReceipts)
            {
                throw new InvalidConfigurationException(
                    $"EIP-8304 is configured (eip8304TransitionTimestamp={chainSpec.Parameters.Eip8304TransitionTimestamp}) but " +
                    $"Receipt.{nameof(IReceiptConfig.StoreReceipts)} is disabled. Historical receipts must be stored to compute index tables.", -1);
            }

            if (blockTree.Head is null)
                return;

            long headNumber = (long)blockTree.Head.Number;
            int requiredBlocks = Math.Min((int)headNumber, Eip8304Constants.SyncRecoveryBlocks);
            if (requiredBlocks <= 0)
                return;

            long startBlock = headNumber - requiredBlocks + 1;
            for (long n = startBlock; n <= headNumber; n++)
            {
                Block block = blockTree.FindBlock((ulong)n, BlockTreeLookupOptions.None)
                    ?? throw new InvalidOperationException(
                        $"Cannot initialize EIP-8304: block body {n} is missing from the block tree. " +
                        $"At least {Eip8304Constants.SyncRecoveryBlocks} blocks of bodies and receipts must be available before head block {headNumber}.");

                if (receiptStorage is not null && block.Transactions.Length > 0)
                {
                    TxReceipt[]? receipts = receiptStorage.Get(block);
                    if (receipts is null || receipts.Length != block.Transactions.Length)
                    {
                        throw new InvalidOperationException(
                            $"Cannot initialize EIP-8304: receipts for block {n} ({block.Hash}) are missing from receipt storage. " +
                            $"At least {Eip8304Constants.SyncRecoveryBlocks} blocks of bodies and receipts must be available before head block {headNumber}.");
                    }
                }
            }
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
