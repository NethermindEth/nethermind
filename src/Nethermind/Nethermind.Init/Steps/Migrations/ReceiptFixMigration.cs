// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.Peers;
using Polly;

namespace Nethermind.Init.Steps.Migrations
{
    public class ReceiptFixMigration(
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        ISyncPeerPool syncPeerPool,
        ISyncConfig syncConfig,
        ILogManager logManager) : IDatabaseMigration
    {
        private readonly ILogger _logger = logManager.GetClassLogger<ReceiptFixMigration>();

        public async Task Run(CancellationToken cancellationToken)
        {
            if (syncConfig.FixReceipts)
            {
                MissingReceiptsFixVisitor visitor = new(
                    syncConfig.AncientReceiptsBarrierCalc,
                    blockTree.Head?.Number - 2 ?? 0,
                    receiptStorage,
                    logManager,
                    syncPeerPool,
                    blockTree,
                    cancellationToken
                );

                try
                {
                    await blockTree.Accept(visitor, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    if (_logger.IsWarn) _logger.Warn("Fixing receipts in DB canceled.");
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error("Fixing receipts in DB failed.", e);
                }
            }
        }

        private class MissingReceiptsFixVisitor(
            long startLevel,
            long endLevel,
            IReceiptStorage receiptStorage,
            ILogManager logManager,
            ISyncPeerPool syncPeerPool,
            IBlockTree blockTree,
            CancellationToken cancellationToken
            ) : ReceiptsVerificationVisitor(startLevel, endLevel, receiptStorage, logManager)
        {
            private readonly IReceiptStorage _receiptStorage = receiptStorage;
            private readonly ISyncPeerPool _syncPeerPool = syncPeerPool;
            private readonly CancellationToken _cancellationToken = cancellationToken;
            private readonly TimeSpan _delay = TimeSpan.FromSeconds(5);
            private readonly IBlockTree _blockTree = blockTree;

            public override async Task<BlockVisitOutcome> VisitBlock(Block block, CancellationToken cancellationToken)
            {
                BlockVisitOutcome outcome = await base.VisitBlock(block, cancellationToken);

                if (_blockTree.IsMainChain(block.Header))
                {
                    _receiptStorage.EnsureCanonical(block);
                }

                return outcome;
            }

            protected override async Task OnBlockWithoutReceipts(Block block, int transactionsLength, int txReceiptsLength)
            {
                if (_logger.IsInfo)
                    _logger.Info($"Missing receipts for block {block.ToString(Block.Format.FullHashAndNumber)}, expected {transactionsLength} but got {txReceiptsLength}.");

                await Policy.HandleResult<bool>(downloaded => !downloaded)
                    .WaitAndRetryAsync(5, _ => _delay)
                    .ExecuteAsync(async () => await DownloadReceiptsForBlock(block));
            }

            private async Task<bool> DownloadReceiptsForBlock(Block block)
            {
                if (block.Hash is null)
                {
                    throw new ArgumentException("Cannot download receipts for a block without a known hash.");
                }

                FastBlocksAllocationStrategy strategy = new(TransferSpeedType.Receipts, block.Number, true);
                SyncPeerAllocation peer = await _syncPeerPool.Allocate(strategy, AllocationContexts.Receipts);
                ISyncPeer? currentSyncPeer = peer.Current?.SyncPeer;
                if (currentSyncPeer is not null)
                {
                    try
                    {
                        using IOwnedReadOnlyList<TxReceipt[]?> receipts = await currentSyncPeer.GetReceipts(new List<Hash256> { block.Hash }, _cancellationToken);
                        TxReceipt[]? txReceipts = receipts.FirstOrDefault();
                        if (txReceipts is not null)
                        {
                            _receiptStorage.Insert(block, txReceipts);
                            if (_logger.IsInfo) _logger.Info($"Downloaded missing receipts for block {block.ToString(Block.Format.FullHashAndNumber)}.");
                            return true;
                        }
                        else
                        {
                            if (_logger.IsInfo) _logger.Error($"Fail to download missing receipts for block {block.ToString(Block.Format.FullHashAndNumber)}.");
                        }
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsInfo) _logger.Error($"Fail to download missing receipts for block {block.ToString(Block.Format.FullHashAndNumber)}.", e);
                    }
                    finally
                    {
                        _syncPeerPool.Free(peer);
                    }
                }
                else
                {
                    if (_logger.IsInfo) _logger.Error($"Fail to download missing receipts for block {block.ToString(Block.Format.FullHashAndNumber)}. No peer available.");
                }

                return false;
            }
        }
    }
}
