/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Timers;
using Nethermind.Logging;
using Nethermind.Stats;

namespace Nethermind.Blockchain.Synchronization
{
    public class SyncReport : ISyncReport
    {
        private readonly ISyncConfig _syncConfig;
        private readonly ILogger _logger;

        private SyncPeersReport _syncPeersReport;
        private int _reportId = 0;
        private int _syncReportFrequency = 1;
        private int _syncShortPeersReportFrequency = 60;
        private int _syncFullPeersReportFrequency = 120;

        public double TickTime
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        public SyncReport(IEthSyncPeerPool syncPeerPool, INodeStatsManager nodeStatsManager, ISyncConfig syncConfig, ILogManager logManager, double tickTime = 1000)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncPeersReport = new SyncPeersReport(syncPeerPool, nodeStatsManager, logManager);
            
            StartTime = DateTime.UtcNow;
            CurrentSyncMode = SyncMode.DbSync;

            TickTime = tickTime;
            _timer.Interval = TickTime;
            _timer.Elapsed += TimerOnElapsed;
            _timer.Start();
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
        }

        private Timer _timer = new Timer();

        public SyncMode CurrentSyncMode { get; set; }

        public long FastBlocksPivotNumber { get; set; }

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

            if (!_reportedFastBlocksSummary && FastBlocksHeaders.HasEnded && FastBlocksBodies.HasEnded && FastBlocksReceipts.HasEnded)
            {
                _reportedFastBlocksSummary = true;
                WriteFastBlocksReport();
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
                    WriteFullSyncReport("Fast Blocks Recent");
                    break;
                case SyncMode.FastBlocks:
                    WriteFastBlocksReport();
                    break;
            }
        }

        private void WriteNotStartedReport()
        {
            _logger.Info($"Sync not started yet... {(DateTime.UtcNow - StartTime).Seconds}s");
        }

        private void WriteFullSyncReport(string prefix)
        {
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

            _logger.Info($"Fast Blocks Sync | Headers  {Pad(FastBlocksHeaders.CurrentValue, blockPaddingLength)} / {Pad(FastBlocksPivotNumber, blockPaddingLength)} | current {Pad(FastBlocksHeaders.CurrentPerSecond, speedPaddingLength)}bps | total {Pad(FastBlocksHeaders.TotalPerSecond, speedPaddingLength)}bps");

            if (_syncConfig.DownloadBodiesInFastSync)
            {
                _logger.Info($"Fast Blocks Sync | Bodies   {Pad(FastBlocksBodies.CurrentValue, blockPaddingLength)} / {Pad(FastBlocksPivotNumber, blockPaddingLength)} | current {Pad(FastBlocksBodies.CurrentPerSecond, speedPaddingLength)}bps | total {Pad(FastBlocksBodies.TotalPerSecond, speedPaddingLength)}bps");
            }

            if (_syncConfig.DownloadReceiptsInFastSync)
            {
                _logger.Info($"Fast Blocks Sync | Receipts {Pad(FastBlocksReceipts.CurrentValue, blockPaddingLength)} / {Pad(FastBlocksPivotNumber, blockPaddingLength)} | current {Pad(FastBlocksReceipts.CurrentPerSecond, speedPaddingLength)}bps | total {Pad(FastBlocksReceipts.TotalPerSecond, speedPaddingLength)}bps");
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