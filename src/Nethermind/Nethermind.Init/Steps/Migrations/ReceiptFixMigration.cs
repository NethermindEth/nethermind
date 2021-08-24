//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.Peers;
using Polly;

namespace Nethermind.Init.Steps.Migrations
{
    public class ReceiptFixMigration : IDatabaseMigration
    {
        private readonly IApiWithNetwork _api;
        private Task? _fixTask;
        private CancellationTokenSource? _cancellationTokenSource;

        public ReceiptFixMigration(IApiWithNetwork api)
        {
            _api = api;
        }

        public async ValueTask DisposeAsync()
        {
            _cancellationTokenSource?.Cancel();
            await (_fixTask ?? Task.CompletedTask);
        }

        public void Run()
        {
            ISyncConfig syncConfig = _api.Config<ISyncConfig>();
            var logger = _api.LogManager.GetClassLogger();
            if (syncConfig.FixReceipts && _api.BlockTree != null)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                CancellationToken cancellationToken = _cancellationTokenSource.Token;
                
                var visitor = new MissingReceiptsFixVisitor(
                    syncConfig.PivotNumberParsed,
                    _api.BlockTree.Head?.Number - 2 ?? 0,
                    _api.ReceiptStorage!,
                    _api.LogManager,
                    _api.SyncPeerPool!,
                    cancellationToken);
                
                _fixTask = _api.BlockTree.Accept(visitor, cancellationToken).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (logger.IsError) logger.Error("Fixing receipts in DB failed.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        if (logger.IsWarn) logger.Warn("Fixing receipts in DB canceled.");
                    }
                });
            }
        }

        private class MissingReceiptsFixVisitor : ReceiptsVerificationVisitor
        {
            private readonly IReceiptStorage _receiptStorage;
            private readonly ISyncPeerPool _syncPeerPool;
            private readonly CancellationToken _cancellationToken;
            private readonly TimeSpan _delay;

            public MissingReceiptsFixVisitor(long startLevel, long endLevel, IReceiptStorage receiptStorage, ILogManager logManager, ISyncPeerPool syncPeerPool, CancellationToken cancellationToken) 
                : base(startLevel, endLevel, receiptStorage, logManager)
            {
                _receiptStorage = receiptStorage;
                _syncPeerPool = syncPeerPool;
                _cancellationToken = cancellationToken;
                _delay = TimeSpan.FromSeconds(5);
            }

            protected override async Task OnBlockWithoutReceipts(Block block, int transactionsLength, int txReceiptsLength)
            {
                if (_logger.IsInfo) _logger.Info($"Missing receipts for block {block.ToString(Block.Format.FullHashAndNumber)}, expected {transactionsLength} but got {txReceiptsLength}.");
                
                await Policy.HandleResult<bool>(downloaded => !downloaded)
                    .WaitAndRetryAsync(5, i => _delay)
                    .ExecuteAsync(async () => await DownloadReceiptsForBlock(block));
            }

            private async Task<bool> DownloadReceiptsForBlock(Block block)
            {
                if (block.Hash == null)
                {
                    throw new ArgumentException("Cannot download receipts for a block without a known hash.");
                }
                
                var strategy = new FastBlocksAllocationStrategy(TransferSpeedType.Receipts, block.Number, true);
                SyncPeerAllocation peer = await _syncPeerPool.Allocate(strategy, AllocationContexts.Receipts);
                ISyncPeer? currentSyncPeer = peer.Current?.SyncPeer;
                if (currentSyncPeer != null)
                {
                    try
                    {
                        TxReceipt[][]? receipts = await currentSyncPeer.GetReceipts(new List<Keccak> {block.Hash}, _cancellationToken);
                        TxReceipt[]? txReceipts = receipts?.FirstOrDefault();
                        if (txReceipts != null)
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
