﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.State.Repositories;
using Nethermind.Store.Bloom;
using Timer = System.Timers.Timer;

namespace Nethermind.Runner.Ethereum.Steps.Migrations
{
    public class BloomMigration : IDatabaseMigration
    {
        private static readonly BlockHeader EmptyHeader = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.Zero, 0L, 0L, UInt256.Zero, Bytes.Empty);
        
        private readonly EthereumRunnerContext _context;
        private readonly ILogger _logger;
        private Stopwatch? _stopwatch;
        private readonly MeasuredProgress _progress = new MeasuredProgress();
        private long _migrateCount;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _migrationTask;
        private Average[]? _averages;
        private readonly StringBuilder _builder = new StringBuilder();
        private readonly IBloomConfig _bloomConfig;
        
        public BloomMigration(EthereumRunnerContext context)
        {
            _context = context;
            _logger = context.LogManager.GetClassLogger<BloomMigration>();
            _bloomConfig = context.Config<IBloomConfig>();
        }
        
        public void Run()
        {
            if (_context.BloomStorage == null) throw new StepDependencyException(nameof(_context.BloomStorage));
            if (_context.Synchronizer == null) throw new StepDependencyException(nameof(_context.Synchronizer));

            IBloomStorage? storage = _context.BloomStorage;
            if (storage.NeedsMigration)
            {
                if (_bloomConfig.Migration)
                {
                    if (CanMigrate(_context.Synchronizer.SyncMode))
                    {
                        RunBloomMigration();
                    }
                    else
                    {
                        _context.Synchronizer.SyncModeChanged += SynchronizerOnSyncModeChanged;
                    }
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info($"BloomDb migration disabled. Finding logs in first {MinBlockNumber} blocks might be slow.");
                }
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug("BloomDb migration not needed.");
            }
        }
        
        private bool CanMigrate(SyncMode syncMode)
        {
            switch (syncMode)
            {
                case SyncMode.NotStarted:
                case SyncMode.FastBlocks:
                case SyncMode.Beam:
                    return false;
                default:
                    return true;
            }
        }

        private void SynchronizerOnSyncModeChanged(object? sender, SyncModeChangedEventArgs e)
        {
            if (CanMigrate(e.Current))
            {
                if (_context.Synchronizer == null) throw new StepDependencyException(nameof(_context.Synchronizer));
                RunBloomMigration();
                _context.Synchronizer.SyncModeChanged -= SynchronizerOnSyncModeChanged;
            }
        }

        private void RunBloomMigration()
        {
            if (_context.DisposeStack == null) throw new StepDependencyException(nameof(_context.DisposeStack));
            if (_context.BloomStorage == null) throw new StepDependencyException(nameof(_context.BloomStorage));

            if (_context.BloomStorage.NeedsMigration)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _context.DisposeStack.Push(this);
                _stopwatch = Stopwatch.StartNew();
                _migrationTask = Task.Run(() => RunBloomMigration(_cancellationTokenSource.Token))
                    .ContinueWith(x =>
                    {
                        if (x.IsFaulted && _logger.IsError)
                        {
                            _stopwatch.Stop();
                            _logger.Error(GetLogMessage("failed", $"Error: {x.Exception}"), x.Exception);
                        }
                    });
            }
        }

        private long MinBlockNumber
        {
            get
            {
                if (_context.BloomStorage == null) throw new StepDependencyException(nameof(_context.BloomStorage));
                if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));
                
                return _context.BloomStorage.MinBlockNumber == long.MaxValue
                    ? _context.BlockTree.BestKnownNumber
                    : _context.BloomStorage.MinBlockNumber - 1;
            }
        }

        private void RunBloomMigration(CancellationToken token)
        {
            BlockHeader GetMissingBlockHeader(long i)
            {
                if (_logger.IsWarn) _logger.Warn(GetLogMessage("warning", $"Header for block {i} not found. Logs will not be searchable for this block."));
                return EmptyHeader;
            }

            if (_context.BloomStorage == null) throw new StepDependencyException(nameof(_context.BloomStorage));
            if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));
            if (_context.ChainLevelInfoRepository == null) throw new StepDependencyException(nameof(_context.ChainLevelInfoRepository));
            
            IBlockTree blockTree = _context.BlockTree;
            IBloomStorage storage = _context.BloomStorage;
            long to = MinBlockNumber;
            long synced = storage.MigratedBlockNumber + 1;
            long from = synced;
            _migrateCount = to + 1;
            _averages = _context.BloomStorage.Averages.ToArray();
            var chainLevelInfoRepository = _context.ChainLevelInfoRepository;

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
                    storage.Migrate(GetHeadersForMigration());
                }
                finally
                {
                    _progress.MarkEnd();
                    _stopwatch?.Stop();
                }

                IEnumerable<BlockHeader> GetHeadersForMigration()
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
                    
                    for (long i = from; i <= to; i++)
                    {
                        if (token.IsCancellationRequested)
                        {
                            timer.Stop();
                            if (_logger.IsInfo) _logger.Info(GetLogMessage("cancelled"));
                            yield break;
                        }

                        if (TryGetMainChainBlockHashFromLevel(i, out var blockHash))
                        {
                            var header = blockTree.FindHeader(blockHash, BlockTreeLookupOptions.None);
                            yield return header ?? GetMissingBlockHeader(i);
                        }
                        else
                        {
                            yield return GetMissingBlockHeader(i);
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
            string message = $"BloomDb migration {status} | {_stopwatch?.Elapsed:d\\:hh\\:mm\\:ss} | {_progress.CurrentValue.ToString().PadLeft(_migrateCount.ToString().Length)} / {_migrateCount} blocks migrated. | current {_progress.CurrentPerSecond:F2}bps | total {_progress.TotalPerSecond:F2}bps. {GeAveragesMessage()} {suffix}";
            _progress.SetMeasuringPoint();
            return message;
        }

        private string GeAveragesMessage()
        {
            if (_bloomConfig.MigrationStatistics && _averages != null)
            {
                _builder.Clear();
                _builder.Append("Average bloom saturation: ");
                for (int index = 0; index < _averages.Length; index++)
                {
                    var average = _averages[index];
                    _builder.Append((average.Value / Bloom.BitLength).ToString("P2"));
                    decimal count = 0;
                    decimal length = 0;
                    decimal safeBitCount = Bloom.BitLength * 0.6m;
                    foreach (var bucket in average.Buckets)
                    {
                        if (bucket.Key > safeBitCount)
                        {
                            count += bucket.Value;
                            length += (bucket.Key - safeBitCount) * bucket.Value;
                        }
                    }

                    if (count > 0)
                    {
                        _builder.Append($"(W:{count}, {count / average.Count:P}, L:{length / count:F0})");
                    }

                    _builder.Append('|');
                }

                return _builder.ToString();
            }

            return String.Empty;
        }

        public async ValueTask DisposeAsync()
        {
            _cancellationTokenSource?.Cancel();
            await (_migrationTask ?? Task.CompletedTask);
        }
    }
}