//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.State.Repositories;
using Timer = System.Timers.Timer;

namespace Nethermind.Runner.Ethereum.Steps.Migrations
{
    public class ReceiptMigration : IDatabaseMigration
    {
        private static readonly Block EmptyBlock = new Block(new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.Zero, 0L, 0L, UInt256.Zero, Bytes.Empty));

        private readonly EthereumRunnerContext _context;
        private readonly ILogger _logger;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _migrationTask;
        private Stopwatch _stopwatch;
        private long _toBlock;
        private readonly MeasuredProgress _progress = new MeasuredProgress();

        public ReceiptMigration(EthereumRunnerContext context)
        {
            _context = context;
            _logger = context.LogManager.GetClassLogger<ReceiptMigration>();
        }

        public async ValueTask DisposeAsync()
        {
            _cancellationTokenSource?.Cancel();
            await (_migrationTask ?? Task.CompletedTask);
        }

        public void Run()
        {
            if (_context.ReceiptStorage == null) throw new StepDependencyException(nameof(_context.ReceiptStorage));
            if (_context.DbProvider == null) throw new StepDependencyException(nameof( _context.DbProvider));
            if (_context.DisposeStack == null) throw new StepDependencyException(nameof(_context.DisposeStack));
            if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));

            _toBlock = MigrateToBlockNumber;
            var initConfig = _context.Config<IInitConfig>();
            
            if (_toBlock > 0)
            {
                if (initConfig.ReceiptsMigration)
                {
                    RunMigration();
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info($"ReceiptsDb migration disabled. Finding logs when multiple blocks receipts need to be scanned might be slow.");
                }
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug("ReceiptsDb migration not needed.");
            }
        }

        private void RunMigration()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _context.DisposeStack.Push(this);
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

        private void RunMigration(CancellationToken token)
        {
            Block GetMissingBlock(long i)
            {
                if (_logger.IsWarn) _logger.Warn(GetLogMessage("warning", $"Block {i} not found. Logs will not be searchable for this block."));
                EmptyBlock.Header.Number = i;
                return EmptyBlock;
            }

            IBlockTree blockTree = _context.BlockTree;
            IReceiptStorage? storage =_context.ReceiptStorage;
            long synced = 0;
            IChainLevelInfoRepository? chainLevelInfoRepository = _context.ChainLevelInfoRepository;
            IDb receiptsDb = _context.DbProvider.ReceiptsDb;

            _progress.Update(synced);

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
                        receiptsDb.StartBatch();
                        try
                        {
                            if (block.ReceiptsRoot == Keccak.EmptyTreeHash)
                            {
                                storage.Insert(block, Array.Empty<TxReceipt>());
                            }
                            else
                            {
                                var receipts = storage.Get(block);
                                storage.Insert(block, receipts);
                            }

                            storage.MigratedBlockNumber = block.Number;
                        }
                        finally
                        {
                            receiptsDb.CommitBatch();
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
                        using var batch = chainLevelInfoRepository.StartBatch();
                        var level = chainLevelInfoRepository.LoadLevel(number);
                        if (level != null)
                        {
                            if (!level.HasBlockOnMainChain)
                            {
                                if (level.BlockInfos.Length > 0)
                                {
                                    level.HasBlockOnMainChain = true;
                                    chainLevelInfoRepository.PersistLevel(number, level, batch);
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
                    
                    for (long i = _toBlock - 1; i > 0; i++)
                    {
                        if (token.IsCancellationRequested)
                        {
                            timer.Stop();
                            if (_logger.IsInfo) _logger.Info(GetLogMessage("cancelled"));
                            yield break;
                        }
                        
                        if (TryGetMainChainBlockHashFromLevel(i, out var blockHash))
                        {
                            var header = blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
                            yield return header ?? GetMissingBlock(i);
                        }
                        else
                        {
                            yield return GetMissingBlock(i);
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
            _context.ReceiptStorage.MigratedBlockNumber == long.MaxValue
                ? _context.BlockTree.BestKnownNumber
                : _context.ReceiptStorage.MigratedBlockNumber - 1;
    }
}