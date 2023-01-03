// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Synchronization.Reporting
{
    public interface ISyncReport : IDisposable
    {
        MeasuredProgress FullSyncBlocksDownloaded { get; }

        long FullSyncBlocksKnown { get; set; }

        MeasuredProgress HeadersInQueue { get; }

        MeasuredProgress BodiesInQueue { get; }

        MeasuredProgress ReceiptsInQueue { get; }

        MeasuredProgress FastBlocksHeaders { get; }

        MeasuredProgress FastBlocksBodies { get; }

        MeasuredProgress FastBlocksReceipts { get; }

        MeasuredProgress BeaconHeaders { get; }

        MeasuredProgress BeaconHeadersInQueue { get; }
    }
}
