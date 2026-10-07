// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Config;

public interface IBlocksConfig : IConfig
{

    //[ConfigItem(
    //    Description = "Defines whether the blocks should be produced.",
    //    DefaultValue = "false")]
    //bool Enabled { get; set; }

    [ConfigItem(
        Description = "The block gas limit that the block producer should try to reach in the fastest possible way based on the protocol rules. If not specified, then the block producer should follow others.",
        DefaultValue = "null")]
    ulong? TargetBlockGasLimit { get; set; }

    [ConfigItem(
        Description = "The minimum gas premium (or the gas price before the London hard fork) for transactions accepted by the block producer.",
        DefaultValue = "1")]
    UInt256 MinGasPrice { get; set; }

    [ConfigItem(
        Description = "Whether to change the difficulty of the block randomly within the constraints. Used in NethDev only.",
        DefaultValue = "false")]
    bool RandomizedBlocks { get; set; }

    [ConfigItem(Description = "The block header extra data up to 32 bytes in length.", DefaultValue = "Nethermind")]
    string ExtraData { get; set; }

    [ConfigItem(Description = "The block time slot, in seconds.", DefaultValue = "12")]
    ulong SecondsPerSlot { get; set; }

    [ConfigItem(Description = "The fraction of slot time that can be used for a single block improvement.", DefaultValue = "0.25", HiddenFromDocs = true)]
    double SingleBlockImprovementOfSlot { get; set; }

    [ConfigItem(Description = "State pre-warming level while processing blocks: `None`, `Block` (warm the block's own transactions), or `BlockAndMempool` (also speculatively warm from the mempool between blocks).", DefaultValue = "BlockAndMempool")]
    PreWarmMode PreWarming { get; set; }

    [ConfigItem(
        Description =
            "Budget for the per-block tier of the precompile results cache, in kilobytes, split between the cacheable precompiles. Requires PreWarming. " +
            "Not recommended to set below 1MB when enabled. `0` or negative values disable precompile caching entirely.",
        DefaultValue = "32768", HiddenFromDocs = true
    )]
    int PrecompileCacheMaxKilobytes { get; set; }

    [ConfigItem(Description = "Specify pre-warm state concurrency. Default is logical processor - 1.", DefaultValue = "0", HiddenFromDocs = true)]
    int PreWarmStateConcurrency { get; set; }

    [ConfigItem(Description = "On an Intel hybrid CPU, pin block processing to: `All` logical processors, both hyperthreads of every `Performance` core, one hyperthread of every performance core (`PerformancePhysical`), or one performance core to itself, the first without CPU 0, kept clear of prewarm (`Dedicated`). No effect on other CPUs.", DefaultValue = "Performance", HiddenFromDocs = true)]
    ProcessingCores ProcessingCores { get; set; }

    [ConfigItem(Description = "On an Intel hybrid CPU, keep the pre-warm workers for the transactions block processing reaches next on the performance cores and the rest on the efficiency cores. No effect on other CPUs, nor with `ProcessingCores` set to `All`.", DefaultValue = "true", HiddenFromDocs = true)]
    bool PreWarmCoreSplit { get; set; }

    [ConfigItem(Description = "Concurrency for speculative mempool pre-warming (runs in the idle gap between blocks). Default (0) is half of PreWarmStateConcurrency, to leave cores for RPC.", DefaultValue = "0", HiddenFromDocs = true)]
    int MempoolPreWarmConcurrency { get; set; }

    [ConfigItem(Description = "Read the code of the contracts a block access list names on background threads ahead of execution. Requires PreWarming.", DefaultValue = "false", HiddenFromDocs = true)]
    bool PrefetchBlockAccessListCode { get; set; }

    /// <summary>Whether block processing takes over a transaction's pre-warm run instead of executing it again.</summary>
    /// <remarks>
    /// On by default. Requires <see cref="PreWarming"/>, and applies only on the Ethereum transaction processor, to blocks
    /// without a block access list (EIP-7928) and before EIP-8037.
    /// </remarks>
    [ConfigItem(Description = "Hand pre-warm runs over to block processing. Requires PreWarming. Not applied with a block access list (EIP-7928), from EIP-8037, nor on chains with their own transaction processor.", DefaultValue = "true", HiddenFromDocs = true)]
    bool PreWarmHandoff { get; set; }

    /// <summary>How many queued blocks past the one being processed the prewarmer runs ahead while it executes.</summary>
    /// <remarks>
    /// Only blocks already waiting in the processing queue, as during sync, are run ahead. Their runs read the state the
    /// processed block started from, and are checked again against the real state when their block's turn comes. Requires
    /// <see cref="PreWarmHandoff"/>.
    /// </remarks>
    [ConfigItem(Description = "How many queued blocks the prewarmer runs ahead of block processing, 0 to disable. Requires PreWarmHandoff.", DefaultValue = "0", HiddenFromDocs = true)]
    int PreWarmLookAhead { get; set; }

    /// <summary>Whether blocks suggested by sync are processed off the thread that suggests them.</summary>
    /// <remarks>
    /// An empty processing queue otherwise resumes processing on the writer's thread, which keeps sync from suggesting
    /// the next block, and the recovery loop from recovering its senders, until the block is processed. Detached, they
    /// queue blocks ahead of processing, which <see cref="PreWarmLookAhead"/> needs.
    /// </remarks>
    [ConfigItem(Description = "Process blocks suggested by sync off the suggesting thread, so download and sender recovery run ahead of processing.", DefaultValue = "false", HiddenFromDocs = true)]
    bool DetachSyncProcessing { get; set; }

    [ConfigItem(Description = "How many threads run queued blocks ahead, 0 for half the logical processors. Requires PreWarmLookAhead.", DefaultValue = "0", HiddenFromDocs = true)]
    int PreWarmLookAheadConcurrency { get; set; }

    /// <remarks>
    /// Runs ahead read a state their block's parent may have changed. When fewer than this share still hold at their block's
    /// own pass, the prewarmer runs ahead only now and then, to notice when it pays again.
    /// </remarks>
    [ConfigItem(Description = "The share of runs ahead, in percent, that must still hold for the prewarmer to keep running ahead of every block; 0 to always run ahead. Requires PreWarmLookAhead.", DefaultValue = "30", HiddenFromDocs = true)]
    int PreWarmLookAheadMinHoldPercent { get; set; }

    [ConfigItem(Description = "Keep running queued blocks ahead through the state root, until the commit, instead of stopping with the block's transactions. Requires PreWarmLookAhead.", DefaultValue = "false", HiddenFromDocs = true)]
    bool PreWarmLookAheadUntilCommit { get; set; }

    /// <summary>Whether sync takes over pre-warm runs whose read-modify-written slots moved, checked by the block's roots.</summary>
    /// <remarks>
    /// A counter every transaction bumps makes every run but the first stale. Replaying such a run with its writes shifted by
    /// how far the slot moved is right whenever the transaction did not branch on the value; the block's state and receipts
    /// roots tell whether it did, and a block they reject is processed again without shifting. Only for blocks sync
    /// suggests, whose headers are authenticated by the beacon chain.
    /// </remarks>
    [ConfigItem(Description = "During sync, take over pre-warm runs whose read-modify-written slots moved, verified by the block's roots.", DefaultValue = "false", HiddenFromDocs = true)]
    bool ShiftedSyncReplay { get; set; }

    [ConfigItem(Description = "The block production timeout, in milliseconds.", DefaultValue = "4000")]
    int BlockProductionTimeoutMs { get; set; }

    [ConfigItem(Description = "The genesis block load timeout, in milliseconds.", DefaultValue = "40000")]
    int GenesisTimeoutMs { get; set; }

    [ConfigItem(Description = "The max transaction bytes to add in block production, in kilobytes.", DefaultValue = "7936")]
    long BlockProductionMaxTxKilobytes { get; set; }

    [ConfigItem(Description = "The ticker that gas rewards are denominated in for processing logs", DefaultValue = "ETH", HiddenFromDocs = true)]
    string GasToken { get; set; }

    [ConfigItem(Description = "Builds blocks on main (non-readonly) state", DefaultValue = "false", HiddenFromDocs = true)]
    bool BuildBlocksOnMainState { get; set; }

    [ConfigItem(
        Description = "Parallelize transaction execution when Block Level Access Lists are available. Experimental Amsterdam/BAL path; disabling falls back to sequential execution and the option is ignored for blocks without BAL bodies.",
        DefaultValue = "true")]
    bool ParallelExecution { get; set; }

    [ConfigItem(
        Description = "Use parallel state reads when Block Level Access Lists are available. Experimental Amsterdam/BAL path; disabling falls back to sequential reads and the option is ignored for blocks without BAL bodies.",
        DefaultValue = "true")]
    bool ParallelExecutionBatchRead { get; set; }

    byte[] GetExtraDataBytes();

    [ConfigItem(Description = "The max blob count after which the block producer should stop adding blobs. Minimum value is `0`.", DefaultValue = "null")]
    int? BlockProductionBlobLimit { get; set; }

    [ConfigItem(
        Description = "The threshold in milliseconds for logging slow block diagnostics. " +
                      "Blocks processed slower than this value are logged with detailed JSON metrics. " +
                      "Set to `0` to log all blocks. Set to `-1` to disable slow block logging entirely.",
        DefaultValue = "-1")]
    long SlowBlockThresholdMs { get; set; }

    [ConfigItem(
        Description = "The per-transaction threshold in milliseconds for detailed transaction-level logging within slow blocks. " +
                      "Transactions slower than this value are included individually in the slow block JSON log. " +
                      "Set to `0` to log all transactions. Set to `-1` to disable per-transaction logging.",
        DefaultValue = "-1")]
    long SlowBlockPerTxThresholdMs { get; set; }

    [ConfigItem(
        Description = "The maximum block gas assumed to be supported. " +
                      "Used to inherit some RLP limits. ",
        DefaultValue = "1000000000",
        HiddenFromDocs = true)]
    ulong MaxGasLimit { get; set; }
}
