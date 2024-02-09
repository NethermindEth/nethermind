// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Synchronization;

public interface ISyncConfig : IConfig
{
    [ConfigItem(Description = "Whether to connect to peers and sync.", DefaultValue = "true")]
    bool NetworkingEnabled { get; set; }

    [ConfigItem(Description = "Whether to download and process new blocks.", DefaultValue = "true")]
    bool SynchronizationEnabled { get; set; }

    [ConfigItem(
        Description = "Whether to use the Fast sync mode (the eth/63 synchronization algorithm).",
        DefaultValue = "false")]
    bool FastSync { get; set; }

    // Minimum is taken from MultiSyncModeSelector.StickyStateNodesDelta
    [ConfigItem(Description = "In Fast sync mode, the min height threshold limit up to which the Full sync, if already on, stays on when the chain is behind the network head. If the limit is exceeded, it switches back to Fast sync. For regular usage scenarios, setting this value lower than 32 is not recommended as this can cause issues with chain reorgs. Note that the last 2 blocks are always processed in Full sync, so setting it lower than 2 has no effect.", DefaultValue = "8192")]
    long? FastSyncCatchUpHeightDelta { get; set; }

    [ConfigItem(Description = "Whether to first download blocks from the provided pivot number downwards in the Fast sync mode. This allows for parallelization of requests with many sync peers and with no need to worry about syncing a valid branch (syncing downwards to 0). You need to provide the pivot block number, hash, and total difficulty from a trusted source (e.g., Etherscan) and confirm with other sources if you want to change it.", DefaultValue = "false")]
    bool FastBlocks { get; set; }

    [ConfigItem(Description = "Whether to make smaller requests, in Fast Blocks mode, to avoid Geth from disconnecting. On the Geth-heavy networks (e.g., Mainnet), it's  a desired behavior while on Nethermind- or OpenEthereum-heavy networks (Goerli, Aura), it slows down the sync by a factor of ~4.", DefaultValue = "true")]
    public bool UseGethLimitsInFastBlocks { get; set; }

    [ConfigItem(Description = "Whether to download the old block headers in the Fast sync mode. If `false`, Nethermind downloads only recent blocks headers.", DefaultValue = "true")]
    bool DownloadHeadersInFastSync { get; set; }

    [ConfigItem(Description = "Whether to download the block bodies in the Fast sync mode.", DefaultValue = "true")]
    bool DownloadBodiesInFastSync { get; set; }

    [ConfigItem(Description = "Whether to download receipts in the Fast sync mode. This slows down the process by a few hours but allows to interact with dApps that perform extensive historical logs searches.", DefaultValue = "true")]
    bool DownloadReceiptsInFastSync { get; set; }

    [ConfigItem(Description = "The total difficulty of the pivot block for the Fast sync mode.", DefaultValue = "null")]
    string PivotTotalDifficulty { get; }

    [ConfigItem(Description = "The number of the pivot block for the Fast sync mode.", DefaultValue = "0")]
    string PivotNumber { get; set; }

    [ConfigItem(Description = "The hash of the pivot block for the Fast sync mode.", DefaultValue = "null")]
    string? PivotHash { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "0")]
    long PivotNumberParsed => LongConverter.FromString(PivotNumber);

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "0")]
    UInt256 PivotTotalDifficultyParsed => UInt256.Parse(PivotTotalDifficulty ?? "0");

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true)]
    Hash256? PivotHashParsed => PivotHash is null ? null : new Hash256(Bytes.FromHexString(PivotHash));

    [ConfigItem(Description = "The max number of attempts to update the pivot block based on the FCU message from the consensus client.", DefaultValue = "2147483647")]
    int MaxAttemptsToUpdatePivot { get; set; }

    [ConfigItem(Description = $$"""
        _Experimental._ The earliest body downloaded with fast sync when `{{nameof(DownloadBodiesInFastSync)}}` is set to `true`. The actual value is determined as follows:

        ```
        max{ 1, min{ PivotNumber, AncientBodiesBarrier } }
        ```

        """,
        DefaultValue = "0")]
    public long AncientBodiesBarrier { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "1")]
    public long AncientBodiesBarrierCalc => Math.Max(1, Math.Min(PivotNumberParsed, AncientBodiesBarrier));

    [ConfigItem(Description = $$"""
        _Experimental._ The earliest receipt downloaded with fast sync when `{{nameof(DownloadReceiptsInFastSync)}}` is set to `true`. The actual value is determined as folows:

        ```
        max{ 1, min{ PivotNumber, max{ AncientBodiesBarrier, AncientReceiptsBarrier } } }
        ```

        """,
        DefaultValue = "0")]
    public long AncientReceiptsBarrier { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "1")]
    public long AncientReceiptsBarrierCalc => Math.Max(1, Math.Min(PivotNumberParsed, Math.Max(AncientBodiesBarrier, AncientReceiptsBarrier)));

    [ConfigItem(Description = "Whether to enable the Witness protocol.", DefaultValue = "false")]
    public bool WitnessProtocolEnabled { get; set; }

    [ConfigItem(Description = "Whether to use the Snap sync mode.", DefaultValue = "false")]
    public bool SnapSync { get; set; }

    [ConfigItem(Description = "The number of account range partitions to create. Increases the Snap sync request concurrency. Allowed values are between between 1 and 256.", DefaultValue = "8")]
    int SnapSyncAccountRangePartitionCount { get; set; }

    [ConfigItem(Description = "Whether to enable receipts validation that checks for receipts that might be missing because of a bug. If needed, receipts are downloaded from the network. If `true`, the pivot number must be same one used originally as it's used as a cut-off point.", DefaultValue = "false")]
    public bool FixReceipts { get; set; }

    [ConfigItem(Description = $"Whether to recalculate the total difficulty from `{nameof(FixTotalDifficultyStartingBlock)}` to `{nameof(FixTotalDifficultyLastBlock)}`.", DefaultValue = "false")]
    public bool FixTotalDifficulty { get; set; }

    [ConfigItem(Description = "The first block to recalculate the total difficulty for.", DefaultValue = "1")]
    public long FixTotalDifficultyStartingBlock { get; set; }

    [ConfigItem(Description = "The last block to recalculate the total difficulty for. If not specified, the best known block is used.\n", DefaultValue = "null")]
    public long? FixTotalDifficultyLastBlock { get; set; }

    [ConfigItem(Description = "Whether to disable some optimizations and do a more extensive sync. Useful when sync state is corrupted.", DefaultValue = "false")]
    public bool StrictMode { get; set; }

    [ConfigItem(Description = $"_Experimental._ Whether to operate as a non-validator. If `true`, the `{nameof(DownloadReceiptsInFastSync)}` and `{nameof(DownloadBodiesInFastSync)}` can be set to `false`.", DefaultValue = "false")]
    public bool NonValidatorNode { get; set; }

    [ConfigItem(Description = "_Experimental._ Configure the database for write optimizations during sync. Significantly reduces the total number of writes and sync time if you are not network limited.", DefaultValue = "HeavyWrite")]
    public ITunableDb.TuneType TuneDbMode { get; set; }

    [ConfigItem(Description = "_Experimental._ Configure the blocks database for write optimizations during sync.", DefaultValue = "EnableBlobFiles")]
    ITunableDb.TuneType BlocksDbTuneDbMode { get; set; }

    [ConfigItem(Description = "The max number of threads used for syncing. `0` to use the number of logical processors.", DefaultValue = "0")]
    public int MaxProcessingThreads { get; set; }

    [ConfigItem(Description = "Enables healing trie from network when state is corrupted.", DefaultValue = "true", HiddenFromDocs = true)]
    public bool TrieHealing { get; set; }

    [ConfigItem(Description = "Whether to shut down Nethermind once sync is finished.", DefaultValue = "false")]
    public bool ExitOnSynced { get; set; }

    [ConfigItem(Description = "The time, in seconds, to wait before shutting down Nethermind once sync is finished.", DefaultValue = "60")]
    public int ExitOnSyncedWaitTimeSec { get; set; }

    [ConfigItem(Description = "Interval, in seconds, between `malloc_trim` calls during sync.", DefaultValue = "300", HiddenFromDocs = true)]
    public int MallocTrimIntervalSec { get; set; }

    [ConfigItem(Description = "Enable snap serving.", DefaultValue = "true", HiddenFromDocs = true)]
    bool SnapServe { get; set; }
}
