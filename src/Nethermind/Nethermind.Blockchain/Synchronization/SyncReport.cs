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
using System.Linq;
using System.Text;
using System.Timers;
using Nethermind.Logging;
using Nethermind.Stats;

namespace Nethermind.Blockchain.Synchronization
{
    public class SyncReport : ISyncReport
    {
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncProgressResolver _syncProgressResolver;
        private readonly ISyncModeSelector _syncModeSelector;
        private readonly ILogger _logger;

        private SyncPeersReport _syncPeersReport;
        private int _reportId = 0;
        private int _syncReportFrequency = 1;
        private int _noProgressStateSyncReportFrequency = 120;
        private int _syncShortPeersReportFrequency = 60;
        private int _syncFullPeersReportFrequency = 120;

        public double TickTime
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        public SyncReport(IEthSyncPeerPool syncPeerPool, INodeStatsManager nodeStatsManager, ISyncConfig syncConfig, ISyncProgressResolver syncProgressResolver, ISyncModeSelector syncModeSelector, ILogManager logManager, double tickTime = 1000)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncProgressResolver = syncProgressResolver ?? throw new ArgumentNullException(nameof(syncProgressResolver));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _syncPeersReport = new SyncPeersReport(syncPeerPool, nodeStatsManager, logManager);
            
            StartTime = DateTime.UtcNow;
            CurrentSyncMode = SyncMode.DbSync;

            TickTime = tickTime;
            _timer.Interval = TickTime;
            _timer.AutoReset = false;
            _timer.Elapsed += TimerOnElapsed;
            _timer.Start();
            
            _syncModeSelector.Changed +=SyncModeSelectorOnChanged;
        }

        private void SyncModeSelectorOnChanged(object sender, SyncModeChangedEventArgs e)
        {
            _logger.Info($"Sync mode changed from {e.Previous} to {e.Current}");
        }

        private DateTime StartTime { get; }

        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            if (_reportId % _syncReportFrequency == 0)
            {
                WriteSyncReport();
            }

            if (_reportId % _syncFullPeersReportFrequency == 0)
            {
                _syncPeersReport.WriteFullReport();
            }
            else if (_reportId % _syncShortPeersReportFrequency == 0)
            {
                _syncPeersReport.WriteShortReport();
            }

            _reportId++;

            _timer.Enabled = true;
        }

        private Timer _timer = new Timer();

        public SyncMode CurrentSyncMode { get; set; }

        public long FastBlocksPivotNumber { get; set; }
        
        public MeasuredProgress HeadersInQueue { get; } = new MeasuredProgress();
        
        public MeasuredProgress BodiesInQueue { get; } = new MeasuredProgress();
        
        public MeasuredProgress ReceiptsInQueue { get; } = new MeasuredProgress();

        public MeasuredProgress FastBlocksHeaders { get; } = new MeasuredProgress();

        public MeasuredProgress FastBlocksBodies { get; } = new MeasuredProgress();

        public MeasuredProgress FastBlocksReceipts { get; } = new MeasuredProgress();

        public MeasuredProgress FastBlocksRemainingBlocks { get; } = new MeasuredProgress();

        public MeasuredProgress FullSyncBlocksDownloaded { get; } = new MeasuredProgress();

        public MeasuredProgress FullSyncBlocksProcessed { get; } = new MeasuredProgress();
        
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

        private bool _reportedFastBlocksSummary = false;

        private void WriteSyncReport()
        {
            if (!_logger.IsInfo)
            {
                return;
            }

            if(_logger.IsDebug) WriteSyncConfigReport();
            
            if (!_reportedFastBlocksSummary && FastBlocksHeaders.HasEnded && FastBlocksBodies.HasEnded && FastBlocksReceipts.HasEnded)
            {
                _reportedFastBlocksSummary = true;
                WriteFastBlocksReport();
            }

            if (!_syncPeerPool.UsefulPeers.Any())
            {
                WriteFullSyncReport($"Waiting for useful peers in '{CurrentSyncMode}' sync mode");
                return;
            }
            
            switch (CurrentSyncMode)
            {
                case SyncMode.NotStarted:
                    WriteNotStartedReport();
                    break;
                case SyncMode.Full:
                    WriteFullSyncReport("Full Sync");
                    break;
                case SyncMode.Headers:
                    WriteFullSyncReport($"Fast Sync From Block {(_syncConfig.FastBlocks ? _syncConfig.PivotNumber : "0")}");
                    break;
                case SyncMode.FastBlocks:
                    WriteFastBlocksReport();
                    break;
                case SyncMode.DbSync:
                    WriteDbSyncReport();
                    break;
                case SyncMode.StateNodes:
                    if (_reportId % _noProgressStateSyncReportFrequency == 0)
                    {
                        WriteStateNodesReport();
                    }
                    break;
                case SyncMode.WaitForProcessor:
                    WriteWaitForProcessorReport();
                    break;
                default:
                    _logger.Info($"Sync mode: {CurrentSyncMode}");
                    break;
            }
        }

        private void WriteSyncConfigReport()
        {
            if (!_logger.IsTrace) return;
            
            bool isFastSync = _syncConfig.FastSync;
            bool isFastBlocks = _syncConfig.FastBlocks;
            bool bodiesInFastBlocks = _syncConfig.DownloadBodiesInFastSync;
            bool receiptsInFastBlocks = _syncConfig.DownloadBodiesInFastSync;

            StringBuilder builder = new StringBuilder();
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
                builder.Append($"Sync config - fast sync without fast blocks");
            }
            else
            {
                builder.Append($"Sync config - full archive sync");
            }
            
            if(_logger.IsTrace) _logger.Trace(builder.ToString());
        }

        private void WriteWaitForProcessorReport()
        {
            _logger.Info($"Waiting for block processor to catch up before syncing further");
        }

        private void WriteStateNodesReport()
        {
            _logger.Info($"Syncing state nodes");
        }

        private void WriteDbSyncReport()
        {
            _logger.Info($"Syncing previously downloaded blocks from DB");
        }

        private void WriteNotStartedReport()
        {
            _logger.Info($"Sync not started yet... {(DateTime.UtcNow - StartTime).Seconds}s");
        }

        private void WriteFullSyncReport(string prefix)
        {
            if (FullSyncBlocksKnown == 0)
            {
                _logger.Info($"{prefix} | Waiting for peers to learn about the head number.");
                return;
            }
            
            if (FullSyncBlocksKnown - FullSyncBlocksDownloaded.CurrentValue < 32)
            {
                return;
            }
            
            _logger.Info($"{prefix} | Downloaded {FullSyncBlocksDownloaded.CurrentValue,9} | current {FullSyncBlocksDownloaded.CurrentPerSecond:F2}bps | total {FullSyncBlocksDownloaded.TotalPerSecond:F2}bps");
            FullSyncBlocksDownloaded.SetMeasuringPoint();
        }

        private void WriteFastBlocksReport()
        {
            int blockPaddingLength = FastBlocksPivotNumber.ToString().Length;
            int speedPaddingLength = 5;
            
            _logger.Info($"Fast Blocks Sync | Peers {_syncPeerPool.UsefulPeerCount} / {_syncPeerPool.PeerCount}");
            _logger.Info($"Fast Blocks Sync | Headers  {Pad(FastBlocksHeaders.CurrentValue, blockPaddingLength)} / {Pad(FastBlocksPivotNumber, blockPaddingLength)} | queue {Pad(HeadersInQueue.CurrentValue, speedPaddingLength)} | current {Pad(FastBlocksHeaders.CurrentPerSecond, speedPaddingLength)}bps | total {Pad(FastBlocksHeaders.TotalPerSecond, speedPaddingLength)}bps");

            if (_syncConfig.DownloadBodiesInFastSync)
            {
                _logger.Info($"Fast Blocks Sync | Bodies   {Pad(FastBlocksBodies.CurrentValue, blockPaddingLength)} / {Pad(FastBlocksPivotNumber, blockPaddingLength)} | queue {Pad(BodiesInQueue.CurrentValue, speedPaddingLength)} | current {Pad(FastBlocksBodies.CurrentPerSecond, speedPaddingLength)}bps | total {Pad(FastBlocksBodies.TotalPerSecond, speedPaddingLength)}bps");
            }

            if (_syncConfig.DownloadReceiptsInFastSync)
            {
                _logger.Info($"Fast Blocks Sync | Receipts {Pad(FastBlocksReceipts.CurrentValue, blockPaddingLength)} / {Pad(FastBlocksPivotNumber, blockPaddingLength)} | queue {Pad(ReceiptsInQueue.CurrentValue, speedPaddingLength)} | current {Pad(FastBlocksReceipts.CurrentPerSecond, speedPaddingLength)}bps | total {Pad(FastBlocksReceipts.TotalPerSecond, speedPaddingLength)}bps");
            }

            FastBlocksHeaders.SetMeasuringPoint();
            FastBlocksBodies.SetMeasuringPoint();
            FastBlocksReceipts.SetMeasuringPoint();
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}