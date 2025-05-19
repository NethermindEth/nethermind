// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.Reporting
{
    public interface ISyncReport : IDisposable
    {
        void SyncModeSelectorOnChanged(object? sender, SyncModeChangedEventArgs e);

        ProgressLogger FullSyncBlocksDownloaded { get; }

        ProgressLogger FastBlocksHeaders { get; }

        ProgressLogger FastBlocksBodies { get; }

        ProgressLogger FastBlocksReceipts { get; }

        ProgressLogger BeaconHeaders { get; }

    }
}
