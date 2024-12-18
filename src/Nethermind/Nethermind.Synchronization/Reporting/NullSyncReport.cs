// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.Reporting
{
    public class NullSyncReport : ISyncReport
    {
        public void Dispose()
        {
        }

        public static NullSyncReport Instance = new();

        public void SyncModeSelectorOnChanged(object? sender, SyncModeChangedEventArgs e)
        {
        }

        public MeasuredProgress FullSyncBlocksDownloaded { get; } = new("", LimboLogs.Instance);
        public long FullSyncBlocksKnown { get; set; }
        public MeasuredProgress FastBlocksHeaders { get; } = new("", LimboLogs.Instance);
        public MeasuredProgress FastBlocksBodies { get; } = new("", LimboLogs.Instance);
        public MeasuredProgress FastBlocksReceipts { get; } = new("", LimboLogs.Instance);
        public MeasuredProgress BeaconHeaders { get; } = new("", LimboLogs.Instance);
    }
}
