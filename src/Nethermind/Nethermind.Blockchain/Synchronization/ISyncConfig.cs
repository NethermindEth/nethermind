// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Synchronization
{
    public interface ISyncConfig : IConfig
    {
        [ConfigItem(Description = "If 'false' then the node does not connect to peers.", DefaultValue = "true")]
        bool NetworkingEnabled { get; set; }

        [ConfigItem(Description = "If 'false' then the node does not download/process new blocks.", DefaultValue = "true")]
        bool SynchronizationEnabled { get; set; }

        [ConfigItem(
            Description = "If set to 'true' then the Fast Sync (eth/63) synchronization algorithm will be used.",
            DefaultValue = "false")]
        bool FastSync { get; set; }

        // Minimum is taken from MultiSyncModeSelector.StickyStateNodesDelta
        [ConfigItem(Description = "Relevant only if 'FastSync' is 'true'. If set to a value, then it will set a minimum height threshold limit up to which FullSync, if already on, will stay on when chain will be behind network. If this limit will be exceeded, it will switch back to FastSync. In normal usage we do not recommend setting this to less than 32 as this can cause issues with chain reorgs. Please note that last 2 blocks will always be processed in FullSync, so setting it to less than 2 will have no effect.", DefaultValue = "8192")]
        long? FastSyncCatchUpHeightDelta { get; set; }

        [ConfigItem(Description = "If set to 'true' then in the Fast Sync mode blocks will be first downloaded from the provided PivotNumber downwards. This allows for parallelization of requests with many sync peers and with no need to worry about syncing a valid branch (syncing downwards to 0). You need to enter the pivot block number, hash and total difficulty from a trusted source (you can use etherscan and confirm with other sources if you wan to change it).", DefaultValue = "false")]
        bool FastBlocks { get; set; }

        [ConfigItem(Description = "If set to 'true' then in the Fast Blocks mode Nethermind generates smaller requests to avoid Geth from disconnecting. On the Geth heavy networks (mainnet) it is desired while on Parity or Nethermind heavy networks (Goerli, AuRa) it slows down the sync by a factor of ~4", DefaultValue = "true")]
        public bool UseGethLimitsInFastBlocks { get; set; }

        [ConfigItem(Description = "If set to 'false' then fast sync will only download recent blocks.", DefaultValue = "true")]
        bool DownloadHeadersInFastSync { get; set; }

        [ConfigItem(Description = "If set to 'true' then the block bodies will be downloaded in the Fast Sync mode.", DefaultValue = "true")]
        bool DownloadBodiesInFastSync { get; set; }

        [ConfigItem(Description = "If set to 'true' then the receipts will be downloaded in the Fast Sync mode. This will slow down the process by a few hours but will allow you to interact with dApps that execute extensive historical logs searches (like Maker CDPs).", DefaultValue = "true")]
        bool DownloadReceiptsInFastSync { get; set; }

        [ConfigItem(Description = "Total Difficulty of the pivot block for the Fast Blocks sync (not - this is total difficulty and not difficulty).", DefaultValue = "null")]
        string PivotTotalDifficulty { get; }

        [ConfigItem(Description = "Number of the pivot block for the Fast Blocks sync.", DefaultValue = "null")]
        string PivotNumber { get; set; }

        [ConfigItem(Description = "Hash of the pivot block for the Fast Blocks sync.", DefaultValue = "null")]
        string PivotHash { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "0")]
        long PivotNumberParsed => LongConverter.FromString(PivotNumber ?? "0");

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "0")]
        UInt256 PivotTotalDifficultyParsed => UInt256.Parse(PivotTotalDifficulty ?? "0");

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
        Keccak PivotHashParsed => PivotHash is null ? null : new Keccak(Bytes.FromHexString(PivotHash));

        [ConfigItem(Description = "[EXPERIMENTAL] Defines the earliest body downloaded in fast sync when DownloadBodiesInFastSync is enabled. Actual values used will be Math.Max(1, Math.Min(PivotNumber, AncientBodiesBarrier))", DefaultValue = "0")]
        public long AncientBodiesBarrier { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "1")]
        public long AncientBodiesBarrierCalc => Math.Max(1, Math.Min(PivotNumberParsed, AncientBodiesBarrier));

        [ConfigItem(Description = "[EXPERIMENTAL] Defines the earliest receipts downloaded in fast sync when DownloadReceiptsInFastSync is enabled. Actual value used will be Math.Max(1, Math.Min(PivotNumber, Math.Max(AncientBodiesBarrier, AncientReceiptsBarrier)))", DefaultValue = "0")]
        public long AncientReceiptsBarrier { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "1")]
        public long AncientReceiptsBarrierCalc => Math.Max(1, Math.Min(PivotNumberParsed, Math.Max(AncientBodiesBarrier, AncientReceiptsBarrier)));

        [ConfigItem(Description = "Enables witness protocol.", DefaultValue = "false")]
        public bool WitnessProtocolEnabled { get; set; }

        [ConfigItem(Description = "Enables SNAP sync protocol.", DefaultValue = "false")]
        public bool SnapSync { get; set; }

        [ConfigItem(Description = "Number of account range partition to create. Increase snap sync request concurrency. Value must be between 1 to 256 (inclusive).", DefaultValue = "8")]
        int SnapSyncAccountRangePartitionCount { get; set; }

        [ConfigItem(Description = "[ONLY FOR MISSING RECEIPTS ISSUE] Turns on receipts validation that checks for ones that might be missing due to previous bug. It downloads them from network if needed." +
                                  "If used please check that PivotNumber is same as original used when syncing the node as its used as a cut-off point.", DefaultValue = "false")]
        public bool FixReceipts { get; set; }

        [ConfigItem(Description = "Disable some optimization and run a more extensive sync. Useful for broken sync state but normally not needed", DefaultValue = "false")]
        public bool StrictMode { get; set; }

        [ConfigItem(Description = "[EXPERIMENTAL] Only for non validator nodes! If set to true, DownloadReceiptsInFastSync and/or DownloadBodiesInFastSync can be set to false.", DefaultValue = "false")]
        public bool NonValidatorNode { get; set; }
    }
}
