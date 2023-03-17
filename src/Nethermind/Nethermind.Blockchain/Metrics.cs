// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.Serialization;
using Nethermind.Core.Attributes;
using Nethermind.Int256;
// ReSharper disable InconsistentNaming

namespace Nethermind.Blockchain
{
    public static class Metrics
    {
        [Description("Total MGas processed")]
        [CounterMetric]
        public static decimal Mgas { get; set; }

        [Description("Total number of transactions processed")]
        [CounterMetric]
        public static long Transactions { get; set; }

        [Description("Total number of blocks processed")]
        [GaugeMetric]
        public static long Blocks { get; set; }

        [Description("Total number of chain reorganizations")]
        [CounterMetric]
        public static long Reorganizations { get; set; }

        [Description("Number of blocks awaiting for recovery of public keys from signatures.")]
        [GaugeMetric]
        public static long RecoveryQueueSize { get; set; }

        [Description("Number of blocks awaiting for processing.")]
        [GaugeMetric]
        public static long ProcessingQueueSize { get; set; }

        [Description("Total number of sealed blocks")]
        [CounterMetric]
        public static long BlocksSealed { get; set; }

        [Description("Total number of failed block seals")]
        [CounterMetric]
        public static long FailedBlockSeals { get; set; }

        [Description("Gas Used in processed blocks")]
        [GaugeMetric]
        public static long GasUsed { get; set; }

        [Description("Gas Limit for processed blocks")]
        [GaugeMetric]
        public static long GasLimit { get; set; }

        [Description("Total difficulty on the chain")]
        [GaugeMetric]
        public static UInt256 TotalDifficulty { get; set; }

        [Description("Difficulty of the last block")]
        [GaugeMetric]
        public static UInt256 LastDifficulty { get; set; }

        [Description("Indicator if blocks can be produced")]
        [GaugeMetric]
        public static long CanProduceBlocks;

        [Description("Number of ms to process the last processed block.")]
        [GaugeMetric]
        public static long LastBlockProcessingTimeInMs;

        //EIP-2159: Common Prometheus Metrics Names for Clients
        [Description("The current height of the canonical chain.")]
        [DataMember(Name = "ethereum_blockchain_height")]
        [GaugeMetric]
        public static long BlockchainHeight { get; set; }

        //EIP-2159: Common Prometheus Metrics Names for Clients
        [Description("The estimated highest block available.")]
        [DataMember(Name = "ethereum_best_known_block_number")]
        [GaugeMetric]
        public static long BestKnownBlockNumber { get; set; }
    }
}
