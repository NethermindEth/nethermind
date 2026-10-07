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

        public async Task Execute(CancellationToken cancellationToken)
        {
            ValidateEip8304Config();
            HealCanonicalChainIfEnabled();
            if (initConfig.ProcessingEnabled)
            {
                await RunBlockTreeInitTasks(cancellationToken);
            }

            // Runs after the init tasks, whose reprocessing may restore the receipts checked here.
            ValidateEip8304History();
        }

        private void ValidateEip8304Config()
        {
            if (chainSpec?.Parameters.Eip8304TransitionTimestamp is not ulong transitionTimestamp)
                return;

            if (receiptConfig is not null && !receiptConfig.StoreReceipts)
                throw Incompatible($"Receipt.{nameof(IReceiptConfig.StoreReceipts)} is disabled");

            if (syncConfig.FastSync)
            {
                if (!syncConfig.DownloadBodiesInFastSync || !syncConfig.DownloadReceiptsInFastSync)
                    throw Incompatible($"Sync.{nameof(ISyncConfig.DownloadBodiesInFastSync)} or Sync.{nameof(ISyncConfig.DownloadReceiptsInFastSync)} is disabled");

                if (syncConfig.AncientReceiptsBarrierCalc > 1 && syncConfig.AncientReceiptsBarrierCalc + (ulong)Eip8304Constants.SyncRecoveryBlocks > syncConfig.PivotNumber)
                    throw Incompatible($"the ancient bodies or receipts barrier ({syncConfig.AncientReceiptsBarrierCalc}) is within {Eip8304Constants.SyncRecoveryBlocks} blocks of the pivot");
            }

            InvalidConfigurationException Incompatible(string reason) => new(
                $"EIP-8304 is configured (eip8304TransitionTimestamp={transitionTimestamp}) but {reason}. " +
                $"Index tables are merged from the bodies and receipts of the last {Eip8304Constants.SyncRecoveryBlocks} blocks.", -1);
        }

        private void ValidateEip8304History()
        {
            if (chainSpec?.Parameters.Eip8304TransitionTimestamp is not ulong transitionTimestamp || blockTree.Head is null)
                return;

            long headNumber = (long)blockTree.Head.Number;
            long startBlock = FirstBlockOfPendingTables(headNumber, transitionTimestamp);
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

        /// <summary>
        /// Returns the first block of the earliest eligible table still to be published after <paramref name="headNumber"/>,
        /// or the block after the head when there is none.
        /// </summary>
        /// <remarks>
        /// Index tables live in memory, so such a table is rebuilt from the bodies and receipts of its blocks up to the head.
        /// A table whose first block precedes activation is never generated, so its history is not needed.
        /// </remarks>
        private long FirstBlockOfPendingTables(long headNumber, ulong transitionTimestamp)
        {
            long startBlock = headNumber + 1;
            for (int level = 1; level < Eip8304Constants.TableSizes.Length; level++)
            {
                int tableSize = Eip8304Constants.TableSizes[level];
                long firstBlock = Math.Max(0, headNumber - tableSize + 2 - (tableSize / 4));
                firstBlock += (tableSize - (firstBlock % tableSize)) % tableSize;

                for (; firstBlock <= headNumber && firstBlock < startBlock; firstBlock += tableSize)
                {
                    BlockHeader? header = blockTree.FindHeader((ulong)firstBlock, BlockTreeLookupOptions.RequireCanonical);
                    if (header is null || header.Timestamp >= transitionTimestamp)
                    {
                        startBlock = firstBlock;
                        break;
                    }
                }
            }

            return startBlock;
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
