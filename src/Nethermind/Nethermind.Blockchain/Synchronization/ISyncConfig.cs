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

    [Obsolete]
    [ConfigItem(Description = "Deprecated.", DefaultValue = "false", HiddenFromDocs = true)]
    bool FastBlocks { get; set; }

    [ConfigItem(Description = "Whether to make smaller requests, in Fast Blocks mode, to avoid Geth from disconnecting. On the Geth-heavy networks (e.g., Mainnet), it's  a desired behavior while on Nethermind- or OpenEthereum-heavy networks (Aura), it slows down the sync by a factor of ~4.", DefaultValue = "true")]
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
    private long PivotNumberParsed => LongConverter.FromString(PivotNumber);

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "0")]
    UInt256 PivotTotalDifficultyParsed => UInt256.Parse(PivotTotalDifficulty ?? "0");

    [ConfigItem(Description = "The max number of attempts to update the pivot block based on the FCU message from the consensus client.", DefaultValue = "2147483647")]
    int MaxAttemptsToUpdatePivot { get; set; }

    [ConfigItem(Description = $$"""
        The earliest body downloaded with fast sync when `{{nameof(DownloadBodiesInFastSync)}}` is set to `true`. The actual value is determined as follows:

        ```
        max{ 1, min{ PivotNumber, AncientBodiesBarrier } }
        ```

        """,
        DefaultValue = "0")]
    public long AncientBodiesBarrier { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "1")]
    public long AncientBodiesBarrierCalc => Math.Max(1, Math.Min(PivotNumberParsed, AncientBodiesBarrier));

    [ConfigItem(Description = $$"""
        The earliest receipt downloaded with fast sync when `{{nameof(DownloadReceiptsInFastSync)}}` is set to `true`. The actual value is determined as follows:

        ```
        max{ 1, min{ PivotNumber, max{ AncientBodiesBarrier, AncientReceiptsBarrier } } }
        ```

        """,
        DefaultValue = "0")]
    public long AncientReceiptsBarrier { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "1")]
    public long AncientReceiptsBarrierCalc => Math.Max(1, Math.Min(PivotNumberParsed, Math.Max(AncientBodiesBarrier, AncientReceiptsBarrier)));

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

    [ConfigItem(Description = $"Whether to operate as a non-validator. If `true`, the `{nameof(DownloadReceiptsInFastSync)}` and `{nameof(DownloadBodiesInFastSync)}` can be set to `false`.", DefaultValue = "false")]
    public bool NonValidatorNode { get; set; }

    [ConfigItem(Description = "Configure the database for write optimizations during sync. Significantly reduces the total number of writes and sync time if you are not network limited.", DefaultValue = nameof(ITunableDb.TuneType.HeavyWrite), HiddenFromDocs = true)]
    public ITunableDb.TuneType TuneDbMode { get; set; }

    [ConfigItem(Description = "Configure the blocks database for write optimizations during sync.", DefaultValue = nameof(ITunableDb.TuneType.EnableBlobFiles), HiddenFromDocs = true)]
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

    [ConfigItem(Description = "_Technical._ Whether to enable snap serving. WARNING: Very slow on hash db layout. Default is to enable on halfpath layout.", DefaultValue = "null", HiddenFromDocs = true)]
    bool? SnapServingEnabled { get; set; }

    [ConfigItem(Description = "_Technical._ MultiSyncModeSelector sync mode timer loop interval. Used for testing.", DefaultValue = "1000", HiddenFromDocs = true)]
    int MultiSyncModeSelectorLoopTimerMs { get; set; }

    [ConfigItem(Description = "_Technical._ SyncDispatcher delay on empty request. Used for testing.", DefaultValue = "10", HiddenFromDocs = true)]
    int SyncDispatcherEmptyRequestDelayMs { get; set; }

    [ConfigItem(Description = "_Technical._ SyncDispatcher allocation timeout. Used for testing.", DefaultValue = "1000", HiddenFromDocs = true)]
    int SyncDispatcherAllocateTimeoutMs { get; set; }

    [ConfigItem(Description = "_Technical._ MultiSyncModeSelector will wait for header to completely sync first.", DefaultValue = "false", HiddenFromDocs = true)]
    bool NeedToWaitForHeader { get; set; }

    [ConfigItem(Description = "_Technical._ Run verify trie on state sync is finished.", DefaultValue = "false", HiddenFromDocs = true)]
    bool VerifyTrieOnStateSyncFinished { get; set; }

    [ConfigItem(Description = "_Technical._ Max distance of state sync from best suggested header.", DefaultValue = "128", HiddenFromDocs = true)]
    int StateMaxDistanceFromHead { get; set; }

    [ConfigItem(Description = "_Technical._ Min distance of state sync from best suggested header.", DefaultValue = "32", HiddenFromDocs = true)]
    int StateMinDistanceFromHead { get; set; }

    [ConfigItem(Description = "_Technical._ Run explicit GC after state sync finished.", DefaultValue = "true", HiddenFromDocs = true)]
    bool GCOnFeedFinished { get; set; }

    [ConfigItem(Description = "_Technical._ Max distance between best suggested header and available state to assume state is synced.", DefaultValue = "0", HiddenFromDocs = true)]
    int HeaderStateDistance { get; set; }

    [ConfigItem(Description = "_Technical._ Memory budget for in memory dependencies of fast headers.", DefaultValue = "0", HiddenFromDocs = true)]
    ulong FastHeadersMemoryBudget { get; set; }

    [ConfigItem(Description = "_Technical._ Enable storage range split.", DefaultValue = "false", HiddenFromDocs = true)]
    bool EnableSnapSyncStorageRangeSplit { get; set; }

    [ConfigItem(Description = "_Technical._ Max tx in forward sync buffer.", DefaultValue = "200000", HiddenFromDocs = true)]
    int MaxTxInForwardSyncBuffer { get; set; }
}
