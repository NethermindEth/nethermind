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

using Nethermind.Config;

namespace Nethermind.Blockchain.Synchronization
{
    [ConfigCategory(Description = "Configuration of the synchronization modes.")]
    public class SyncConfig : ISyncConfig
    {
        private bool _synchronizationEnabled = true;
        
        public static ISyncConfig Default { get; } = new SyncConfig();
        public static ISyncConfig WithFullSyncOnly { get; } = new SyncConfig {FastSync = false, FastBlocks = false};
        public static ISyncConfig WithFastSync { get; } = new SyncConfig {FastSync = true};
        public static ISyncConfig WithFastBlocks { get; } = new SyncConfig {FastSync = true, FastBlocks = true};
        public static ISyncConfig WithEth2Merge { get; } = new SyncConfig {FastSync = false, FastBlocks = false, BlockGossipEnabled = false};

        public bool NetworkingEnabled { get; set; } = true;

        public bool SynchronizationEnabled
        {
            get => NetworkingEnabled && _synchronizationEnabled;
            set => _synchronizationEnabled = value;
        }

        public long? FastSyncCatchUpHeightDelta { get; set; } = 8192;
        public bool FastBlocks { get; set; }
        public bool UseGethLimitsInFastBlocks { get; set; } = true;
        public bool BeamSync { get; set; } = false;
        public bool FastSync { get; set; }
        public bool DownloadHeadersInFastSync { get; set; } = true;
        public bool DownloadBodiesInFastSync { get; set; } = true;
        public bool DownloadReceiptsInFastSync { get; set; } = true;
        public long AncientBodiesBarrier { get; set; }
        public long AncientReceiptsBarrier { get; set; }
        public string PivotTotalDifficulty { get; set; }
        public string PivotNumber { get; set; }
        public string PivotHash { get; set; }
        public int BeamSyncContextTimeout { get; set; } = 4;
        public int BeamSyncPreProcessorTimeout { get; set; } = 15;
        public bool BeamSyncFixMode { get; set; } = false;
        public bool BeamSyncVerifiedMode { get; set; } = false;
        public bool WitnessProtocolEnabled { get; set; } = false;
        public bool FixReceipts { get; set; } = false;
        public bool BlockGossipEnabled { get; set; } = true;
    }
}
