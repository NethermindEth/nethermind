// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Reporting
{
    public class SyncReport : ISyncReport
    {
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly ISyncConfig _syncConfig;
        private readonly IPivot _pivot;
        private readonly ILogger _logger;
        private SyncMode _currentMode = SyncMode.None;

        private readonly SyncPeersReport _syncPeersReport;
        private int _reportId;
        private const int SyncReportFrequency = 1;
        private const int PeerCountFrequency = 15;
        private const int NoProgressStateSyncReportFrequency = 30;
        private const int SyncAllocatedPeersReportFrequency = 30;
        private const int SyncFullPeersReportFrequency = 120;
        private static readonly TimeSpan _defaultReportingIntervals = TimeSpan.FromSeconds(1);

        public SyncReport(ISyncPeerPool syncPeerPool, INodeStatsManager nodeStatsManager, ISyncConfig syncConfig, IPivot pivot, ILogManager logManager, ITimerFactory? timerFactory = null, double tickTime = 1000)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _pivot = pivot ?? throw new ArgumentNullException(nameof(pivot));
            _syncPeersReport = new SyncPeersReport(syncPeerPool, nodeStatsManager, logManager);
            _timer = (timerFactory ?? TimerFactory.Default).CreateTimer(_defaultReportingIntervals);

            FastBlocksHeaders = new("Old Headers", logManager);
            FastBlocksBodies = new("Old Bodies ", logManager);
            FastBlocksReceipts = new("Old Receipts", logManager);
            FullSyncBlocksDownloaded = new("Downloaded", logManager);
            BeaconHeaders = new("Beacon Headers", logManager);

            BeaconHeaders.SetFormat((progress) =>
            {
                long numHeadersToDownload = _pivot.PivotNumber - _pivot.PivotDestinationNumber + 1;
                string skipSectionStr = progress.SkippedPerSecond != -1
                    ? $"skipped {progress.SkippedPerSecond,ProgressLogger.SpeedPaddingLength:N0} Blk/s | "
                    : "";
                return $"Beacon Headers from block {_pivot.PivotDestinationNumber} to block {_pivot.PivotNumber} | "
                       + $"{progress.CurrentValue,ProgressLogger.BlockPaddingLength:N0} / {numHeadersToDownload,ProgressLogger.BlockPaddingLength:N0} | " +
                       $"queue {progress.CurrentQueued,ProgressLogger.QueuePaddingLength:N0} | " +
                       $"current {progress.CurrentPerSecond,ProgressLogger.SpeedPaddingLength:N0} Blk/s  | " +
                       skipSectionStr +
                       $"total {progress.TotalPerSecond,ProgressLogger.SpeedPaddingLength:N0} Blk/s";
            });

            StartTime = DateTime.UtcNow;

            _timer.AutoReset = false;
            _timer.Elapsed += TimerOnElapsed;

            if (_syncConfig.SynchronizationEnabled)
            {
                _timer.Start();
            }
        }

        public void SyncModeSelectorOnChanged(object? sender, SyncModeChangedEventArgs e)
        {
            _currentMode = e.Current;
            if (e.Previous.NotSyncing() && e.Current == SyncMode.Full ||
                e.Previous == SyncMode.Full && e.Current.NotSyncing())
            {
                return;
            }

            if (e.Previous != e.Current)
            {
                // Repeat of "Changing state" so only output as confirm in debug
                if (_logger.IsDebug) _logger.Debug($"Sync mode changed from {e.Previous} to {e.Current}");
            }
        }

        private DateTime StartTime { get; }

        private void TimerOnElapsed(object? sender, EventArgs e)
        {
            if (_reportId % SyncReportFrequency == 0)
            {
                WriteSyncReport();
            }

            if (_reportId % SyncFullPeersReportFrequency == 0)
            {
                _syncPeersReport.WriteFullReport();
            }
            else if (_reportId % SyncAllocatedPeersReportFrequency == 0)
            {
                _syncPeersReport.WriteAllocatedReport();
            }

            _reportId++;

            _timer.Enabled = true;
        }

        private readonly ITimer _timer;

        public ProgressLogger FastBlocksHeaders { get; init; }

        public ProgressLogger FastBlocksBodies { get; init; }

        public ProgressLogger FastBlocksReceipts { get; init; }

        public ProgressLogger FullSyncBlocksDownloaded { get; init; }

        public ProgressLogger BeaconHeaders { get; init; }

        private bool _reportedFastBlocksSummary;
        private uint _nodeInfoType;

        private void WriteSyncReport()
        {
            UpdateMetrics();

            if (!_logger.IsInfo)
            {
                return;
            }

            SyncMode currentSyncMode = _currentMode;
            if (_logger.IsDebug) WriteSyncConfigReport();

            if (!_reportedFastBlocksSummary && FastBlocksHeaders.HasEnded && FastBlocksBodies.HasEnded && FastBlocksReceipts.HasEnded)
            {
                _reportedFastBlocksSummary = true;
                WriteFastBlocksReport(currentSyncMode);
            }

            if ((currentSyncMode | SyncMode.Full) != SyncMode.Full)
            {
                if (_reportId % PeerCountFrequency == 0)
                {
                    if (_nodeInfoType++ % 2 == 0)
                    {
                        _logger.Info(_syncPeersReport.MakeSummaryReportForPeers(_syncPeerPool.InitializedPeers, $"Peers: {_syncPeerPool.PeerCount} | with best block: {_syncPeerPool.InitializedPeersCount}"));
                    }
                    else
                    {
                        _logger.Info(_syncPeersReport.MakeDiversityReportForPeers(_syncPeerPool.InitializedPeers, $"Peers: {_syncPeerPool.PeerCount} | node diversity : "));
                    }
                }
            }

            if (currentSyncMode == SyncMode.Disconnected && _syncConfig.SynchronizationEnabled)
            {
                WriteNotStartedReport();
            }

            if (currentSyncMode == SyncMode.DbLoad)
            {
                WriteDbSyncReport();
            }

            if ((currentSyncMode & SyncMode.StateNodes) == SyncMode.StateNodes)
            {
                if (_reportId % NoProgressStateSyncReportFrequency == 0)
                {
                    WriteStateNodesReport();
                }
            }

            if ((currentSyncMode & SyncMode.FastBlocks) == SyncMode.FastBlocks)
            {
                WriteFastBlocksReport(currentSyncMode);
            }

            if ((currentSyncMode & SyncMode.Full) == SyncMode.Full)
            {
                WriteFullSyncReport();
            }

            if ((currentSyncMode & SyncMode.FastSync) == SyncMode.FastSync)
            {
                WriteFullSyncReport();
            }

            if ((currentSyncMode & SyncMode.BeaconHeaders) == SyncMode.BeaconHeaders)
            {
                WriteBeaconSyncReport();
            }
        }

        private void UpdateMetrics()
        {
            Metrics.FastHeaders = FastBlocksHeaders.CurrentValue;
            Metrics.FastBodies = FastBlocksBodies.CurrentValue;
            Metrics.FastReceipts = FastBlocksReceipts.CurrentValue;
        }

        private void WriteSyncConfigReport()
        {
            if (!_logger.IsTrace) return;

            bool isFastSync = _syncConfig.FastSync;
            bool bodiesInFastBlocks = _syncConfig.DownloadBodiesInFastSync;
            bool receiptsInFastBlocks = _syncConfig.DownloadReceiptsInFastSync;

            StringBuilder builder = new();
            if (isFastSync)
            {
                builder.Append($"Sync config - fast sync with fast blocks from block {_syncConfig.PivotNumber}");
                if (bodiesInFastBlocks)
                {
                    builder.Append(" + bodies");
                }

                if (receiptsInFastBlocks)
                {
                    builder.Append(" + receipts");
                }
            }
            else
            {
                builder.Append("Sync config - full archive sync");
            }

            if (_logger.IsTrace) _logger.Trace(builder.ToString());
        }

        private void WriteStateNodesReport()
        {
            _logger.Info("Syncing state nodes");
        }

        private void WriteDbSyncReport()
        {
            _logger.Info("Syncing previously downloaded blocks from DB (partial offline mode until it finishes)");
        }

        private void WriteNotStartedReport()
        {
            _logger.Info($"Waiting for peers... {Math.Round((DateTime.UtcNow - StartTime).TotalSeconds)}s");
        }

        private void WriteFullSyncReport()
        {
            if (FullSyncBlocksDownloaded.TargetValue == 0)
            {
                return;
            }

            if (FullSyncBlocksDownloaded.TargetValue - FullSyncBlocksDownloaded.CurrentValue < 32)
            {
                return;
            }

            FullSyncBlocksDownloaded.LogProgress();
        }

        private void WriteFastBlocksReport(SyncMode currentSyncMode)
        {
            if ((currentSyncMode & SyncMode.FastHeaders) == SyncMode.FastHeaders && FastBlocksHeaders.HasStarted)
            {
                FastBlocksHeaders.LogProgress();
            }

            if ((currentSyncMode & SyncMode.FastBodies) == SyncMode.FastBodies && FastBlocksBodies.HasStarted)
            {
                FastBlocksBodies.LogProgress();
            }

            if ((currentSyncMode & SyncMode.FastReceipts) == SyncMode.FastReceipts && FastBlocksReceipts.HasStarted)
            {
                FastBlocksReceipts.LogProgress();
            }
        }

        private void WriteBeaconSyncReport()
        {
            BeaconHeaders.LogProgress();
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
