// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Synchronization.Reporting
{
    public class NullSyncReport : ISyncReport
    {
        public void Dispose()
        {
        }

        public static NullSyncReport Instance = new();

        public MeasuredProgress FullSyncBlocksDownloaded { get; } = new();
        public long FullSyncBlocksKnown { get; set; }
        public MeasuredProgress HeadersInQueue { get; } = new();
        public MeasuredProgress BodiesInQueue { get; } = new();
        public MeasuredProgress ReceiptsInQueue { get; } = new();
        public MeasuredProgress FastBlocksHeaders { get; } = new();
        public MeasuredProgress FastBlocksBodies { get; } = new();
        public MeasuredProgress FastBlocksReceipts { get; } = new();
        public MeasuredProgress BeaconHeaders { get; } = new();
        public MeasuredProgress BeaconHeadersInQueue { get; }
    }
}
