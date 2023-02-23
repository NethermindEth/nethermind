// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.Config;
using Nethermind.Core;

namespace Nethermind.Blockchain.Synchronization
{
    [ConfigCategory(Description = "Configuration of the synchronization modes.")]
    public class SyncConfig : ISyncConfig
    {
        private bool _synchronizationEnabled = true;
        private bool _fastSync;

        public static ISyncConfig Default { get; } = new SyncConfig();
        public static ISyncConfig WithFullSyncOnly { get; } = new SyncConfig { FastSync = false, FastBlocks = false };
        public static ISyncConfig WithFastSync { get; } = new SyncConfig { FastSync = true };
        public static ISyncConfig WithFastBlocks { get; } = new SyncConfig { FastSync = true, FastBlocks = true };
        public static ISyncConfig WithEth2Merge { get; } = new SyncConfig { FastSync = false, FastBlocks = false, BlockGossipEnabled = false };

        public bool NetworkingEnabled { get; set; } = true;

        public bool SynchronizationEnabled
        {
            get => NetworkingEnabled && _synchronizationEnabled;
            set => _synchronizationEnabled = value;
        }

        public long? FastSyncCatchUpHeightDelta { get; set; } = 8192;
        public bool FastBlocks { get; set; }
        public bool UseGethLimitsInFastBlocks { get; set; } = true;
        public bool FastSync { get => _fastSync || SnapSync; set => _fastSync = value; }
        public bool DownloadHeadersInFastSync { get; set; } = true;
        public bool DownloadBodiesInFastSync { get; set; } = true;
        public bool DownloadReceiptsInFastSync { get; set; } = true;
        public long AncientBodiesBarrier { get; set; }
        public long AncientReceiptsBarrier { get; set; }
        public string PivotTotalDifficulty { get; set; }
        public string PivotNumber { get; set; }
        public string PivotHash { get; set; }
        public bool WitnessProtocolEnabled { get; set; } = false;
        public bool SnapSync { get; set; } = false;
        public bool FixReceipts { get; set; } = false;
        public bool StrictMode { get; set; } = false;
        public bool BlockGossipEnabled { get; set; } = true;
        public bool NonValidatorNode { get; set; } = false;

        public override string ToString()
        {
            return
                $"SyncConfig details. FastSync {FastSync}, PivotNumber: {PivotNumber} DownloadHeadersInFastSync {DownloadHeadersInFastSync}, DownloadBodiesInFastSync {DownloadBodiesInFastSync}, DownloadReceiptsInFastSync {DownloadReceiptsInFastSync}, AncientBodiesBarrier {AncientBodiesBarrier}, AncientReceiptsBarrier {AncientReceiptsBarrier}";
        }

    }
}
