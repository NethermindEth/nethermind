// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db.Blooms;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using Nethermind.Synchronization.ParallelSync;
using Timer = System.Timers.Timer;

namespace Nethermind.Init.Steps.Migrations
{
    public class BloomMigration : IDatabaseMigration
    {
        private static readonly BlockHeader EmptyHeader = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.Zero, 0L, 0L, 0UL, Array.Empty<byte>());

        private readonly IApiWithNetwork _api;
        private readonly ILogger _logger;
        private Stopwatch? _stopwatch;
        private readonly MeasuredProgress _progress = new MeasuredProgress();
        private long _migrateCount;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _migrationTask;
        private Average[]? _averages;
        private readonly StringBuilder _builder = new StringBuilder();
        private readonly IBloomConfig _bloomConfig;

        public BloomMigration(IApiWithNetwork api)
        {
            _api = api;
            _logger = api.LogManager.GetClassLogger<BloomMigration>();
            _bloomConfig = api.Config<IBloomConfig>();
        }

        public void Run()
        {
            if (_api.BloomStorage is null) throw new StepDependencyException(nameof(_api.BloomStorage));
            if (_api.Synchronizer is null) throw new StepDependencyException(nameof(_api.Synchronizer));
            if (_api.SyncModeSelector is null) throw new StepDependencyException(nameof(_api.SyncModeSelector));

            IBloomStorage? storage = _api.BloomStorage;
            if (storage.NeedsMigration)
            {
                if (_bloomConfig.Migration)
                {
                    if (CanMigrate(_api.SyncModeSelector.Current))
                    {
                        RunBloomMigration();
                    }
                    else
                    {
                        _api.SyncModeSelector.Changed += SynchronizerOnSyncModeChanged;
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

        private bool CanMigrate(SyncMode syncMode) => syncMode.NotSyncing();

        private void SynchronizerOnSyncModeChanged(object? sender, SyncModeChangedEventArgs e)
        {
            if (CanMigrate(e.Current))
            {
                if (_api.SyncModeSelector is null) throw new StepDependencyException(nameof(_api.SyncModeSelector));

                RunBloomMigration();
                _api.SyncModeSelector.Changed -= SynchronizerOnSyncModeChanged;
            }
        }

        private void RunBloomMigration()
        {
            if (_api.DisposeStack is null) throw new StepDependencyException(nameof(_api.DisposeStack));
            if (_api.BloomStorage is null) throw new StepDependencyException(nameof(_api.BloomStorage));

            if (_api.BloomStorage.NeedsMigration)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _api.DisposeStack.Push(this);
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
                if (_api.BloomStorage is null) throw new StepDependencyException(nameof(_api.BloomStorage));
                if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));

                return _api.BloomStorage.MinBlockNumber == long.MaxValue
                    ? _api.BlockTree.BestKnownNumber
                    : _api.BloomStorage.MinBlockNumber - 1;
            }
        }

        private void RunBloomMigration(CancellationToken token)
        {
            BlockHeader GetMissingBlockHeader(long i)
            {
                if (_logger.IsWarn) _logger.Warn(GetLogMessage("warning", $"Header for block {i} not found. Logs will not be searchable for this block."));
                return EmptyHeader;
            }

            if (_api.BloomStorage is null) throw new StepDependencyException(nameof(_api.BloomStorage));
            if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.ChainLevelInfoRepository is null) throw new StepDependencyException(nameof(_api.ChainLevelInfoRepository));

            IBlockTree blockTree = _api.BlockTree;
            IBloomStorage storage = _api.BloomStorage;
            long to = MinBlockNumber;
            long synced = storage.MigratedBlockNumber + 1;
            long from = synced;
            _migrateCount = to + 1;
            _averages = _api.BloomStorage.Averages.ToArray();
            IChainLevelInfoRepository? chainLevelInfoRepository = _api.ChainLevelInfoRepository;

            _progress.Update(synced);

            if (_logger.IsInfo) _logger.Info(GetLogMessage("started"));

            using (Timer timer = new Timer(1000) { Enabled = true })
            {
                timer.Elapsed += (_, _) =>
                {
                    if (_logger.IsInfo) _logger.Info(GetLogMessage("in progress"));
                };

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
                        using BatchWrite batch = chainLevelInfoRepository.StartBatch();
                        ChainLevelInfo? level = chainLevelInfoRepository.LoadLevel(number);
                        if (level is not null)
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
                            return blockHash is not null;
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

                        if (TryGetMainChainBlockHashFromLevel(i, out Keccak? blockHash))
                        {
                            BlockHeader? header = blockTree.FindHeader(blockHash!, BlockTreeLookupOptions.None);
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
            string message = $"BloomDb migration {status} | {_stopwatch?.Elapsed:d\\:hh\\:mm\\:ss} | {_progress.CurrentValue.ToString().PadLeft(_migrateCount.ToString().Length)} / {_migrateCount} blocks migrated. | current {_progress.CurrentPerSecond:F2} Blk/s | total {_progress.TotalPerSecond:F2} Blk/s. {GeAveragesMessage()} {suffix}";
            _progress.SetMeasuringPoint();
            return message;
        }

        private string GeAveragesMessage()
        {
            if (_bloomConfig.MigrationStatistics && _averages is not null)
            {
                _builder.Clear();
                _builder.Append("Average bloom saturation: ");
                for (int index = 0; index < _averages.Length; index++)
                {
                    Average average = _averages[index];
                    _builder.Append((average.Value / Bloom.BitLength).ToString("P2"));
                    decimal count = 0;
                    decimal length = 0;
                    decimal safeBitCount = Bloom.BitLength * 0.6m;
                    foreach (KeyValuePair<uint, uint> bucket in average.Buckets)
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
