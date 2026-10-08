// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.Serialization;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;
using Nethermind.Int256;

// ReSharper disable InconsistentNaming

namespace Nethermind.Blockchain;

public static class Metrics
{
    [CounterMetric]
    [Description("Total MGas processed")]
    public static double Mgas { get; set; }

    [GaugeMetric]
    [Description("MGas processed per second")]
    public static double MgasPerSec { get; set; }

    [CounterMetric]
    [Description("Total number of transactions processed")]
    public static long Transactions { get; set; }

    [GaugeMetric]
    [Description("Total number of blocks processed")]
    public static ulong Blocks;

    [CounterMetric]
    [Description("Total number of chain reorganizations")]
    public static long Reorganizations { get; set; }

    [GaugeMetric]
    [Description("Number of receipt bodies retained in memory while awaiting durable state-history capture (receipt derivation only).")]
    public static long RetainedReceiptBodies { get; set; }

    [GaugeMetric]
    [Description("Estimated bytes held by the receipt bodies retained while awaiting durable state-history capture (receipt derivation only).")]
    public static long RetainedReceiptBodyBytes { get; set; }

    [GaugeMetric]
    [Description("Number of blocks awaiting for recovery of public keys from signatures.")]
    public static long RecoveryQueueSize { get; set; }

    [GaugeMetric]
    [Description("Number of blocks awaiting for processing.")]
    public static long ProcessingQueueSize { get; set; }

    [CounterMetric]
    [Description("Total number of sealed blocks")]
    public static long BlocksSealed { get; set; }

    [CounterMetric]
    [Description("Total number of failed block seals")]
    public static long FailedBlockSeals { get; set; }

    [CounterMetric]
    [Description("Transactions block processing took over from their pre-warm run")]
    public static long PrewarmHandoffs { get; set; }

    [CounterMetric]
    [Description("Transactions executed because state their pre-warm run read had changed")]
    public static long PrewarmHandoffsRejected { get; set; }

    [CounterMetric]
    [Description("Transactions executed for lack of a usable pre-warm run")]
    public static long PrewarmHandoffsMissing { get; set; }

    [CounterMetric]
    [Description("Transactions executed after their pre-warm run failed to apply")]
    public static long PrewarmHandoffFailures { get; set; }

    [CounterMetric]
    [Description("Warm runs that first found and read the storage they miss")]
    public static long PrewarmDiscoverFirstRuns;

    [CounterMetric]
    [Description("Runs that only read the caches to find the storage a warm run misses")]
    public static long PrewarmDiscoverFirstRounds;

    [CounterMetric]
    [Description("Storage cells read side by side ahead of warm runs")]
    public static long PrewarmDiscoverFirstCells;

    [CounterMetric]
    [Description("Warm runs that did not find their storage first because recent reads came from memory")]
    public static long PrewarmDiscoverFirstSkipped;

    [CounterMetric]
    [Description("Processed blocks with an even number")]
    public static long EvenBlocksProcessed;

    [CounterMetric]
    [Description("Microseconds spent processing blocks with an even number")]
    public static long EvenBlocksProcessingMicros;

    [CounterMetric]
    [Description("Microseconds of state hashing in blocks with an even number")]
    public static long EvenBlocksStateHashMicros;

    [CounterMetric]
    [Description("Processed blocks with an odd number")]
    public static long OddBlocksProcessed;

    [CounterMetric]
    [Description("Microseconds spent processing blocks with an odd number")]
    public static long OddBlocksProcessingMicros;

    [CounterMetric]
    [Description("Microseconds of state hashing in blocks with an odd number")]
    public static long OddBlocksStateHashMicros;

    [CounterMetric]
    [Description("Blocks whose processing prepared the pre-block caches")]
    public static long PrewarmBlockStarts;

    [CounterMetric]
    [Description("Microseconds block processing spent stopping the mempool pre-warm session and preparing the pre-block caches")]
    public static long PrewarmBlockStartMicros;

    [CounterMetric]
    [Description("Blocks that found the pre-block caches filled for another state, so they started from empty caches")]
    public static long PrewarmBlockStartsUncarried;

    [CounterMetric]
    [Description("Times the block's state opened while a mempool pre-warm session might run")]
    public static long PrewarmConsumerOpens;

    [CounterMetric]
    [Description("Microseconds the block thread spent stopping the mempool pre-warm session as the block's state opened")]
    public static long PrewarmConsumerOpenMicros;

    [CounterMetric]
    [Description("Mempool pre-warm sessions stopped without waiting as the block's state opened, and waited for later")]
    public static long PrewarmSpeculativeJoinsDeferred;

    [CounterMetric]
    [Description("Times a block stopped and joined the mempool pre-warm session")]
    public static long PrewarmSpeculativeJoins;

    [CounterMetric]
    [Description("Microseconds blocks waited to stop and join the mempool pre-warm session")]
    public static long PrewarmSpeculativeJoinMicros;

    [CounterMetric]
    [Description("Mempool pre-warm passes that warmed anything")]
    public static long PrewarmSpeculativePasses;

    [CounterMetric]
    [Description("Transactions the mempool pre-warm passes warmed")]
    public static long PrewarmSpeculativeTxs;

    [CounterMetric]
    [Description("Transactions of the blocks pre-warmed for processing")]
    public static long PrewarmBlockTxs;

    [CounterMetric]
    [Description("Transactions of the blocks pre-warmed for processing that the mempool pre-warm had already warmed")]
    public static long PrewarmBlockTxsMempoolWarmed;

    [CounterMetric]
    [Description("Blocks that took over the mempool pre-warm session's caches")]
    public static long PrewarmMempoolHandoffs;

    [CounterMetric]
    [Description("Blocks that found no mempool pre-warm session to take over, or one for another parent")]
    public static long PrewarmMempoolHandoffMisses;

    [CounterMetric]
    [Description("Pre-warm runs taken to be refreshed because an earlier transaction leaves a slot they read at another value")]
    public static long PrewarmRefreshes { get; set; }

    [CounterMetric]
    [Description("Refreshes not run: a slot the run read is not tracked, or none of them moved")]
    public static long PrewarmRefreshesSkipped { get; set; }

    [CounterMetric]
    [Description("Refreshes stopped because block processing reached their transaction")]
    public static long PrewarmRefreshesCancelled { get; set; }

    [CounterMetric]
    [Description("Refreshes whose run left no footprint")]
    public static long PrewarmRefreshesFailed { get; set; }

    [CounterMetric]
    [Description("Refreshed pre-warm runs stored for block processing to take over")]
    public static long PrewarmRefreshesStored { get; set; }

    [CounterMetric]
    [Description("Transactions block processing took over from a refreshed pre-warm run")]
    public static long PrewarmRefreshesTakenOver { get; set; }

    [GaugeMetric]
    [Description("Gas Used in processed blocks")]
    public static ulong GasUsed { get; set; }

    [GaugeMetric]
    [Description("Gas Limit for processed blocks")]
    public static ulong GasLimit { get; set; }

    [GaugeMetric]
    [Description("Total difficulty on the chain")]
    public static UInt256 TotalDifficulty { get; set; }

    [GaugeMetric]
    [Description("Difficulty of the last block")]
    public static UInt256 LastDifficulty { get; set; }

    [GaugeMetric]
    [Description("Indicator if blocks can be produced")]
    public static long CanProduceBlocks;

    [GaugeMetric]
    [Description("Number of ms to process the last processed block.")]
    public static long LastBlockProcessingTimeInMs;

    //EIP-2159: Common Prometheus Metrics Names for Clients
    [GaugeMetric]
    [Description("The current height of the canonical chain.")]
    [DataMember(Name = "ethereum_blockchain_height")]
    public static ulong BlockchainHeight;

    //EIP-2159: Common Prometheus Metrics Names for Clients
    [GaugeMetric]
    [Description("The estimated highest block available.")]
    [DataMember(Name = "ethereum_best_known_block_number")]
    public static ulong BestKnownBlockNumber;

    [GaugeMetric]
    [Description("Number of invalid blocks.")]
    public static long BadBlocks;

    [GaugeMetric]
    [Description("Number of invalid blocks with extra data set to 'Nethermind'.")]
    public static long BadBlocksByNethermindNodes;

    [GaugeMetric]
    [Description("State root calculation time")]
    public static double StateMerkleizationTime { get; set; }

    [DetailedMetric]
    [ExponentialPowerHistogramMetric(Start = 10, Factor = 1.2, Count = 35)]
    [Description("Histogram of block MGas per second")]
    public static IMetricObserver BlockMGasPerSec { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [ExponentialPowerHistogramMetric(Start = 100, Factor = 1.25, Count = 50)]
    [Description("Histogram of block processing time")]
    public static IMetricObserver BlockProcessingTimeMicros { get; set; } = new NoopMetricObserver();
}
