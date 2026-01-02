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
        private static readonly BlockHeader EmptyHeader = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.Zero, 0L, 0L, 0UL, []);

        private readonly IApiWithNetwork _api;
        private readonly ILogger _logger;
        private Stopwatch? _stopwatch;
        private readonly ProgressLogger _progressLogger;
        private long _migrateCount;
        private Average[]? _averages;
        private readonly StringBuilder _builder = new StringBuilder();
        private readonly IBloomConfig _bloomConfig;

        public BloomMigration(IApiWithNetwork api)
        {
            _api = api;
            _logger = api.LogManager.GetClassLogger<BloomMigration>();
            _progressLogger = new ProgressLogger("Bloom migration ", api.LogManager);
            _bloomConfig = api.Config<IBloomConfig>();
        }

        public async Task Run(CancellationToken cancellationToken)
        {
            if (_api.BloomStorage is null) throw new StepDependencyException(nameof(_api.BloomStorage));
            if (_api.SyncModeSelector is null) throw new StepDependencyException(nameof(_api.SyncModeSelector));

            IBloomStorage? storage = _api.BloomStorage;
            if (storage.NeedsMigration)
            {
                if (_bloomConfig.Migration)
                {
                    await _api.SyncModeSelector.WaitUntilMode(CanMigrate, cancellationToken);

                    _stopwatch = Stopwatch.StartNew();
                    try
                    {
                        RunBloomMigration(cancellationToken);
                    }
                    catch (Exception e)
                    {
                        _stopwatch.Stop();
                        _logger.Error(GetLogMessage("failed", $"Error: {e}"), e);
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

        private static bool CanMigrate(SyncMode syncMode) => syncMode.NotSyncing();

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

            _progressLogger.Update(synced);

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
                    _progressLogger.MarkEnd();
                    _stopwatch?.Stop();
                }

                IEnumerable<BlockHeader> GetHeadersForMigration()
                {
                    bool TryGetMainChainBlockHashFromLevel(long number, out Hash256? blockHash)
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

                        if (TryGetMainChainBlockHashFromLevel(i, out Hash256? blockHash))
                        {
                            BlockHeader? header = blockTree.FindHeader(blockHash!, BlockTreeLookupOptions.None);
                            yield return header ?? GetMissingBlockHeader(i);
                        }
                        else
                        {
                            yield return GetMissingBlockHeader(i);
                        }

                        _progressLogger.Update(++synced);
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
            string message = $"BloomDb migration {status} | {_stopwatch?.Elapsed:d\\:hh\\:mm\\:ss} | {_progressLogger.CurrentValue.ToString().PadLeft(_migrateCount.ToString().Length)} / {_migrateCount} blocks migrated. | current {_progressLogger.CurrentPerSecond:F2} Blk/s | total {_progressLogger.TotalPerSecond:F2} Blk/s. {GeAveragesMessage()} {suffix}";
            _progressLogger.SetMeasuringPoint();
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
    }
}
