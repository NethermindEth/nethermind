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

        public ProgressLogger FullSyncBlocksDownloaded { get; } = new("", LimboLogs.Instance);
        public long FullSyncBlocksKnown { get; set; }
        public ProgressLogger FastBlocksHeaders { get; } = new("", LimboLogs.Instance);
        public ProgressLogger FastBlocksBodies { get; } = new("", LimboLogs.Instance);
        public ProgressLogger FastBlocksReceipts { get; } = new("", LimboLogs.Instance);
        public ProgressLogger BeaconHeaders { get; } = new("", LimboLogs.Instance);
    }
}
