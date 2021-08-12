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
        private const int SpeedPaddingLength = 5;
        
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncModeSelector _syncModeSelector;
        private readonly ILogger _logger;

        private SyncPeersReport _syncPeersReport;
        private int _reportId;
        private const int SyncReportFrequency = 1;
        private const int PeerCountFrequency = 15;
        private const int NoProgressStateSyncReportFrequency = 30;
        private const int SyncShortPeersReportFrequency = 30;
        private const int SyncFullPeersReportFrequency = 120;

        public double TickTime
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        public SyncReport(ISyncPeerPool syncPeerPool, INodeStatsManager nodeStatsManager, ISyncModeSelector syncModeSelector, ISyncConfig syncConfig, ILogManager logManager, double tickTime = 1000)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _syncPeersReport = new SyncPeersReport(syncPeerPool, nodeStatsManager, logManager);

            _fastBlocksPivotNumber = _syncConfig.PivotNumberParsed;
            _blockPaddingLength = _fastBlocksPivotNumber.ToString().Length;
            _paddedPivot = $"{Pad(_fastBlocksPivotNumber, _blockPaddingLength)}";
            
            StartTime = DateTime.UtcNow;

            TickTime = tickTime;
            _timer.Interval = TickTime;
            _timer.AutoReset = false;
            _timer.Elapsed += TimerOnElapsed;

            if (_syncConfig.SynchronizationEnabled)
            {
                _timer.Start();
            }

            _syncModeSelector.Changed += SyncModeSelectorOnChanged;
        }

        private void SyncModeSelectorOnChanged(object? sender, SyncModeChangedEventArgs e)
        {
            if (e.Previous.NotSyncing() && e.Current == SyncMode.Full ||
                e.Previous == SyncMode.Full && e.Current.NotSyncing())
            {
                return;
            }
            
            if (e.Previous != e.Current)
            {
                if (_logger.IsInfo) _logger.Info($"Sync mode changed from {e.Previous} to {e.Current}");
            }
        }

        private DateTime StartTime { get; }

        private void TimerOnElapsed(object? sender, ElapsedEventArgs e)
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

        private readonly Timer _timer = new();

        private long _fastBlocksPivotNumber;

        public MeasuredProgress HeadersInQueue { get; } = new();

        public MeasuredProgress BodiesInQueue { get; } = new();

        public MeasuredProgress ReceiptsInQueue { get; } = new();

        public MeasuredProgress FastBlocksHeaders { get; } = new();

        public MeasuredProgress FastBlocksBodies { get; } = new();

        public MeasuredProgress FastBlocksReceipts { get; } = new();

        public MeasuredProgress FullSyncBlocksDownloaded { get; } = new();

        public long FullSyncBlocksKnown { get; set; }

        private static string Pad(decimal value, int length)
        {
            string valueString = $"{value:F2}";
            return valueString.PadLeft(length + 3, ' ');
        }

        private static string Pad(long value, int length)
        {
            string valueString = $"{value}";
            return valueString.PadLeft(length, ' ');
        }

        private bool _reportedFastBlocksSummary;
        private readonly int _blockPaddingLength;
        private readonly string _paddedPivot;

        private void WriteSyncReport()
        {
            UpdateMetrics();
            
            if (!_logger.IsInfo)
            {
                return;
            }

            SyncMode currentSyncMode = _syncModeSelector.Current;
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
                    _logger.Info($"Peers | with known best block: {_syncPeerPool.InitializedPeersCount} | all: {_syncPeerPool.PeerCount} |");
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
            
            if ((currentSyncMode & SyncMode.Beam) == SyncMode.Beam)
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
            if (!_logger.IsTrace) return;

            bool isFastSync = _syncConfig.FastSync;
            bool isFastBlocks = _syncConfig.FastBlocks;
            bool bodiesInFastBlocks = _syncConfig.DownloadBodiesInFastSync;
            bool receiptsInFastBlocks = _syncConfig.DownloadReceiptsInFastSync;

            StringBuilder builder = new();
            if (isFastSync && isFastBlocks)
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
            else if (isFastSync)
            {
                builder.Append("Sync config - fast sync without fast blocks");
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

            _logger.Info($"Downloaded {Pad(FullSyncBlocksDownloaded.CurrentValue,_blockPaddingLength)} / {Pad(FullSyncBlocksKnown,_blockPaddingLength)} | current {Pad(FullSyncBlocksDownloaded.CurrentPerSecond, SpeedPaddingLength)}bps | total {Pad(FullSyncBlocksDownloaded.TotalPerSecond, SpeedPaddingLength)}bps");
            FullSyncBlocksDownloaded.SetMeasuringPoint();
        }
    
        private void WriteFastBlocksReport(SyncMode currentSyncMode)
        {
            if ((currentSyncMode & SyncMode.FastHeaders) == SyncMode.FastHeaders)
            {
                _logger.Info($"Old Headers  {Pad(FastBlocksHeaders.CurrentValue, _blockPaddingLength)} / {_paddedPivot} | queue {Pad(HeadersInQueue.CurrentValue, SpeedPaddingLength)} | current {Pad(FastBlocksHeaders.CurrentPerSecond, SpeedPaddingLength)}bps | total {Pad(FastBlocksHeaders.TotalPerSecond, SpeedPaddingLength)}bps");
                FastBlocksHeaders.SetMeasuringPoint();
            }

            if ((currentSyncMode & SyncMode.FastBodies) == SyncMode.FastBodies)
            {
                _logger.Info($"Old Bodies   {Pad(FastBlocksBodies.CurrentValue, _blockPaddingLength)} / {_paddedPivot} | queue {Pad(BodiesInQueue.CurrentValue, SpeedPaddingLength)} | current {Pad(FastBlocksBodies.CurrentPerSecond, SpeedPaddingLength)}bps | total {Pad(FastBlocksBodies.TotalPerSecond, SpeedPaddingLength)}bps");
                FastBlocksBodies.SetMeasuringPoint();
            }

            if ((currentSyncMode & SyncMode.FastReceipts) == SyncMode.FastReceipts)
            {
                _logger.Info($"Old Receipts {Pad(FastBlocksReceipts.CurrentValue, _blockPaddingLength)} / {_paddedPivot} | queue {Pad(ReceiptsInQueue.CurrentValue, SpeedPaddingLength)} | current {Pad(FastBlocksReceipts.CurrentPerSecond, SpeedPaddingLength)}bps | total {Pad(FastBlocksReceipts.TotalPerSecond, SpeedPaddingLength)}bps");
                FastBlocksReceipts.SetMeasuringPoint();
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
