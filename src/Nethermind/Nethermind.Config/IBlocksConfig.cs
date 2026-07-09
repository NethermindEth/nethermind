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

    [ConfigItem(Description = "Whether to cache precompile results when processing blocks.", DefaultValue = "True", HiddenFromDocs = true)]
    bool CachePrecompilesOnBlockProcessing { get; set; }

    [ConfigItem(Description = "Specify pre-warm state concurrency. Default is logical processor - 1.", DefaultValue = "0", HiddenFromDocs = true)]
    int PreWarmStateConcurrency { get; set; }

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
