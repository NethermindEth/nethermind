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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using Nethermind.Synchronization.ParallelSync;
using Timer = System.Timers.Timer;

namespace Nethermind.Init.Steps.Migrations
{
    public class ReceiptMigration : IDatabaseMigration, IReceiptsMigration
    {
        private static readonly Block EmptyBlock = new Block(new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.Zero, 0L, 0L, UInt256.Zero, Array.Empty<byte>()));

        private readonly ILogger _logger;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _migrationTask;
        private Stopwatch? _stopwatch;
        private long _toBlock;
        private readonly MeasuredProgress _progress = new MeasuredProgress();
        [NotNull]
        private readonly IReceiptStorage? _receiptStorage;
        [NotNull]
        private readonly IDbProvider? _dbProvider;
        [NotNull]
        private readonly DisposableStack? _disposeStack;
        [NotNull]
        private readonly IBlockTree? _blockTree;
        [NotNull]
        private readonly ISyncModeSelector? _syncModeSelector;
        [NotNull]
        private readonly IChainLevelInfoRepository? _chainLevelInfoRepository;

        private readonly IInitConfig _initConfig;

        public ReceiptMigration(IApiWithNetwork api)
        {
            _logger = api.LogManager.GetClassLogger<ReceiptMigration>();
            _receiptStorage = api.ReceiptStorage ?? throw new StepDependencyException(nameof(api.ReceiptStorage));
            _dbProvider = api.DbProvider ?? throw new StepDependencyException(nameof(api.DbProvider));
            _disposeStack = api.DisposeStack ?? throw new StepDependencyException(nameof(api.DisposeStack));
            _blockTree = api.BlockTree ?? throw new StepDependencyException(nameof(api.BlockTree));
            _syncModeSelector = api.SyncModeSelector ?? throw new StepDependencyException(nameof(api.SyncModeSelector));
            _chainLevelInfoRepository = api.ChainLevelInfoRepository ?? throw new StepDependencyException(nameof(api.ChainLevelInfoRepository));
            _initConfig = api.Config<IInitConfig>() ?? throw new StepDependencyException("initConfig");
        }

        public async ValueTask DisposeAsync()
        {
            _cancellationTokenSource?.Cancel();
            await (_migrationTask ?? Task.CompletedTask);
        }
        
        public async Task<bool> Run(long blockNumber)
        {
            _cancellationTokenSource?.Cancel();
            await (_migrationTask ?? Task.CompletedTask);
            _receiptStorage.MigratedBlockNumber = Math.Min(Math.Max(_receiptStorage.MigratedBlockNumber, blockNumber), (_blockTree.Head?.Number ?? 0) + 1);
            Run();
            return _initConfig.StoreReceipts && _initConfig.ReceiptsMigration;
        }

        public void Run()
        {
            if (_initConfig.StoreReceipts)
            {
                if (_initConfig.ReceiptsMigration)
                {
                    if (CanMigrate(_syncModeSelector.Current))
                    {
                        RunMigration();
                    }
                    else
                    {
                        _syncModeSelector.Changed -= OnSyncModeChanged;
                        _syncModeSelector.Changed += OnSyncModeChanged;
                        if (_logger.IsInfo) _logger.Info($"ReceiptsDb migration will start after switching to full sync.");
                    }
                }
                else
                {
                    long migrateToBlockNumber = MigrateToBlockNumber;
                    if (migrateToBlockNumber > 0)
                    {
                        if (_logger.IsInfo) _logger.Info($"ReceiptsDb migration disabled. Finding logs when multiple blocks receipts need to be scanned might be slow below {migrateToBlockNumber} block.");
                    }
                }
            }
        }
        
        private bool CanMigrate(SyncMode syncMode) => syncMode.NotSyncing();

        private void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs e)
        {
            if (CanMigrate(e.Current))
            {
                RunMigration();
                _syncModeSelector.Changed -= OnSyncModeChanged;
            }
        }

        private void RunMigration()
        {
            _toBlock = MigrateToBlockNumber;
            
            if (_toBlock > 0)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _disposeStack.Push(this);
                _stopwatch = Stopwatch.StartNew();
                _migrationTask = Task.Run(() => RunMigration(_cancellationTokenSource.Token))
                    .ContinueWith(x =>
                    {
                        if (x.IsFaulted && _logger.IsError)
                        {
                            _stopwatch.Stop();
                            _logger.Error(GetLogMessage("failed", $"Error: {x.Exception}"), x.Exception);
                        }
                    });
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug("ReceiptsDb migration not needed.");
            }
        }

        private void RunMigration(CancellationToken token)
        {
            Block GetMissingBlock(long i, Keccak? blockHash)
            {
                if (_logger.IsWarn) _logger.Warn(GetLogMessage("warning", $"Block {i} not found. Logs will not be searchable for this block."));
                EmptyBlock.Header.Number = i;
                EmptyBlock.Header.Hash = blockHash;
                return EmptyBlock;
            }

            long synced = 1;
            IDb receiptsDb = _dbProvider.ReceiptsDb;
            
            _progress.Reset(synced);

            if (_logger.IsInfo) _logger.Info(GetLogMessage("started"));

            using (var timer = new Timer(1000) {Enabled = true})
            {
                timer.Elapsed += (ElapsedEventHandler) ((o, e) =>
                {
                    if (_logger.IsInfo) _logger.Info(GetLogMessage("in progress"));
                });

                try
                {
                    foreach (var block in GetBlockBodiesForMigration())
                    {
                        TxReceipt?[] receipts = _receiptStorage.Get(block);
                        TxReceipt[] notNullReceipts = receipts.Length == 0 ? receipts : receipts.Where(r => r != null).ToArray();

                        if (receipts.Length == 0 || notNullReceipts.Length != 0) // if notNullReceipts.Length is 0 and receipts are not 0 - we are missing all receipts, they are not processed yet.
                        {
                            _receiptStorage.Insert(block, notNullReceipts);
                            _receiptStorage.MigratedBlockNumber = block.Number;
                            
                            for (int i = 0; i < notNullReceipts.Length; i++)
                            {
                                receiptsDb.Delete(notNullReceipts[i].TxHash!);
                            }
                            
                            if (notNullReceipts.Length != receipts.Length)
                            {
                                if(_logger.IsWarn) _logger.Warn(GetLogMessage("warning", $"Block {block.ToString(Block.Format.FullHashAndNumber)} is missing {receipts.Length - notNullReceipts.Length} of {receipts.Length} receipts!"));
                            }
                        }
                        else if (block.Number <= _blockTree.Head?.Number)
                        {
                            if(_logger.IsWarn) _logger.Warn(GetLogMessage("warning", $"Block {block.ToString(Block.Format.FullHashAndNumber)} is missing {receipts.Length - notNullReceipts.Length} of {receipts.Length} receipts!"));
                        }
                    }
                }
                finally
                {
                    _progress.MarkEnd();
                    _stopwatch?.Stop();
                }

                IEnumerable<Block> GetBlockBodiesForMigration()
                {
                    bool TryGetMainChainBlockHashFromLevel(long number, out Keccak? blockHash)
                    {
                        using var batch = _chainLevelInfoRepository.StartBatch();
                        var level = _chainLevelInfoRepository.LoadLevel(number);
                        if (level != null)
                        {
                            if (!level.HasBlockOnMainChain)
                            {
                                if (level.BlockInfos.Length > 0)
                                {
                                    level.HasBlockOnMainChain = true;
                                    _chainLevelInfoRepository.PersistLevel(number, level, batch);
                                }
                            }
                                
                            blockHash = level.MainChainBlock?.BlockHash;
                            return blockHash != null;
                        }
                        else
                        {
                            blockHash = null;
                            return false;
                        }
                    }
                    
                    for (long i = _toBlock - 1; i > 0; i--)
                    {
                        if (token.IsCancellationRequested)
                        {
                            timer.Stop();
                            if (_logger.IsInfo) _logger.Info(GetLogMessage("cancelled"));
                            yield break;
                        }
                        
                        if (TryGetMainChainBlockHashFromLevel(i, out Keccak? blockHash))
                        {
                            var header = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
                            yield return header ?? GetMissingBlock(i, blockHash);
                        }

                        _progress.Update(++synced);
                    }
                }
            }

            if (!token.IsCancellationRequested)
            {
                if (_logger.IsInfo) _logger.Info(GetLogMessage("finished"));
            }
        }

        private string GetLogMessage(string status, string? suffix = null)
        {
            string message = $"ReceiptsDb migration {status} | {_stopwatch?.Elapsed:d\\:hh\\:mm\\:ss} | {_progress.CurrentValue.ToString().PadLeft(_toBlock.ToString().Length)} / {_toBlock} blocks migrated. | current {_progress.CurrentPerSecond:F2}bps | total {_progress.TotalPerSecond:F2}bps. {suffix}";
            _progress.SetMeasuringPoint();
            return message;
        }

        private long MigrateToBlockNumber =>
            _receiptStorage.MigratedBlockNumber == long.MaxValue
                ? _syncModeSelector.Current.NotSyncing() 
                    ? _blockTree.Head?.Number ?? 0
                    : _blockTree.BestKnownNumber
                : _receiptStorage.MigratedBlockNumber - 1;
    }
}
