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
using System.Text;
using System.Timers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
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
        private readonly ISyncModeSelector _syncModeSelector;
        private readonly ILogger _logger;

        private readonly Timer _timer;
        private SyncPeersReport _syncPeersReport;
        private int _reportId;
        private const int SyncReportFrequency = 1;
        private const int NoProgressStateSyncReportFrequency = 30;
        private const int SyncShortPeersReportFrequency = 30;
        private const int SyncFullPeersReportFrequency = 120;


        private static readonly string s_fastBlocksReportPattern = "Old {0,-9} {1,12:N0} / {2,12:N0} | queue {3,6:N0} | current {4,9:N2}bps | total {5,9:N2}bps";
        private static readonly string s_fullSyncReportPattern = "Downloaded    {0,12:N0} / {1,12:N0} |              | current {2,9:N2}bps | total {3,9:N2}bps";

        public SyncReport(ISyncPeerPool syncPeerPool, INodeStatsManager nodeStatsManager, ISyncModeSelector syncModeSelector, ISyncConfig syncConfig, ILogManager logManager, double tickTime = 1000)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _syncPeersReport = new SyncPeersReport(syncPeerPool, nodeStatsManager, logManager);

            StartTime = DateTime.UtcNow;

            if (_syncConfig.SynchronizationEnabled)
            {
                _timer = new Timer { Interval = tickTime, AutoReset = false };
                _timer.Elapsed += TimerOnElapsed;
                _timer.Start();
            }

            _syncModeSelector.Changed += SyncModeSelectorOnChanged;
        }

        private void SyncModeSelectorOnChanged(object sender, SyncModeChangedEventArgs e)
        {
            if (e.Previous == SyncMode.None && e.Current == SyncMode.Full ||
                e.Previous == SyncMode.Full && e.Current == SyncMode.None)
            {
                return;
            }

            if (e.Previous != e.Current)
            {
                if (_logger.IsInfo) _logger.Info($"Sync mode changed from {e.Previous} to {e.Current}");
            }
        }

        private DateTime StartTime { get; }

        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            if (_reportId % SyncReportFrequency == 0)
            {
                WriteSyncReport();
            }

            if (_reportId % SyncFullPeersReportFrequency == 0)
            {
                _syncPeersReport.WriteFullReport();
            }
            else if (_reportId % SyncShortPeersReportFrequency == 0)
            {
                _syncPeersReport.WriteShortReport();
            }

            _reportId++;

            _timer.Enabled = true;
        }


        public MeasuredProgress HeadersInQueue { get; } = new MeasuredProgress();

        public MeasuredProgress BodiesInQueue { get; } = new MeasuredProgress();

        public MeasuredProgress ReceiptsInQueue { get; } = new MeasuredProgress();

        public MeasuredProgress FastBlocksHeaders { get; } = new MeasuredProgress();

        public MeasuredProgress FastBlocksBodies { get; } = new MeasuredProgress();

        public MeasuredProgress FastBlocksReceipts { get; } = new MeasuredProgress();

        public MeasuredProgress FullSyncBlocksDownloaded { get; } = new MeasuredProgress();

        public long FullSyncBlocksKnown { get; set; }

        private bool _reportedFastBlocksSummary;

        private void WriteSyncReport()
        {
            UpdateMetrics();

            if (!_logger.IsInfo) return;
            if (_logger.IsTrace) WriteSyncConfigReport();

            SyncMode currentSyncMode = _syncModeSelector.Current;
            if (!_reportedFastBlocksSummary && FastBlocksHeaders.HasEnded && FastBlocksBodies.HasEnded && FastBlocksReceipts.HasEnded)
            {
                _reportedFastBlocksSummary = true;
                WriteFastBlocksReport(currentSyncMode);
            }

            if (!currentSyncMode.HasFlag(SyncMode.Full))
            {
                _logger.Info($"Peers | with known best block: {_syncPeerPool.InitializedPeersCount} | all: {_syncPeerPool.PeerCount}");
            }

            if (currentSyncMode.Equals(SyncMode.None) && _syncPeerPool.InitializedPeersCount == 0)
            {
                WriteNotStartedReport();
            }

            if (currentSyncMode.HasFlag(SyncMode.DbLoad))
            {
                WriteDbSyncReport();
            }

            if (currentSyncMode.HasFlag(SyncMode.StateNodes))
            {
                if (_reportId % NoProgressStateSyncReportFrequency == 0)
                {
                    WriteStateNodesReport();
                }
            }

            if (currentSyncMode.HasFlag(SyncMode.FastBlocks))
            {
                WriteFastBlocksReport(currentSyncMode);
            }

            if (currentSyncMode.HasFlag(SyncMode.Full | SyncMode.FastSync))
            {
                WriteFullSyncReport();
            }

            if (currentSyncMode.HasFlag(SyncMode.Beam))
            {
                _logger.Info("Beam Sync is ON - you can query the latest state");
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
            StringBuilder builder = new StringBuilder("Sync config -");
            if (_syncConfig.FastSync)
            {
                _ = builder.Append(" fast sync");
                _ = _syncConfig.FastBlocks
                    ? builder.Append($" with fast blocks from block {_syncConfig.PivotNumber}")
                        .Append(_syncConfig.DownloadBodiesInFastSync ? " + bodies" : string.Empty)
                        .Append(_syncConfig.DownloadReceiptsInFastSync ? " + receipts" : string.Empty)
                    : builder.Append(" without fast blocks");
            }
            else
            {
                _ = builder.Append(" full archive sync");
            }

            _logger.Trace(builder.ToString());
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
            _logger.Info($"Waiting for peers... {(DateTime.UtcNow - StartTime).Seconds}s");
        }

        private void WriteFullSyncReport()
        {
            if (FullSyncBlocksKnown == 0)
            {
                return;
            }

            if (FullSyncBlocksKnown - FullSyncBlocksDownloaded.CurrentValue < 32)
            {
                return;
            }

            _logger.Info(string.Format(s_fullSyncReportPattern, FullSyncBlocksDownloaded.CurrentValue, FullSyncBlocksKnown, FullSyncBlocksDownloaded.CurrentPerSecond, FullSyncBlocksDownloaded.TotalPerSecond));
            FullSyncBlocksDownloaded.SetMeasuringPoint();
        }

        private void WriteFastBlocksReport(SyncMode currentSyncMode)
        {
            if ((currentSyncMode & SyncMode.FastHeaders) == SyncMode.FastHeaders)
            {
                _logger.Info(string.Format(s_fastBlocksReportPattern, "Headers", FastBlocksHeaders.CurrentValue, _syncConfig.PivotNumberParsed, HeadersInQueue.CurrentValue, FastBlocksHeaders.CurrentPerSecond, FastBlocksHeaders.TotalPerSecond));
                FastBlocksHeaders.SetMeasuringPoint();
            }

            if ((currentSyncMode & SyncMode.FastBodies) == SyncMode.FastBodies)
            {
                _logger.Info(string.Format(s_fastBlocksReportPattern, "Bodies", FastBlocksBodies.CurrentValue, _syncConfig.PivotNumberParsed, BodiesInQueue.CurrentValue, FastBlocksBodies.CurrentPerSecond, FastBlocksBodies.TotalPerSecond));
                FastBlocksBodies.SetMeasuringPoint();
            }

            if ((currentSyncMode & SyncMode.FastReceipts) == SyncMode.FastReceipts)
            {
                _logger.Info(string.Format(s_fastBlocksReportPattern, "Receipts", FastBlocksReceipts.CurrentValue, _syncConfig.PivotNumberParsed, ReceiptsInQueue.CurrentValue, FastBlocksReceipts.CurrentPerSecond, FastBlocksReceipts.TotalPerSecond));
                FastBlocksReceipts.SetMeasuringPoint();
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
