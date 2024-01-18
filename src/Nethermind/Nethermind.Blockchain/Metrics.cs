// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Runtime.Serialization;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
// ReSharper disable InconsistentNaming

namespace Nethermind.Blockchain
{
    public static class Metrics
    {
        [CounterMetric]
        [Description("Total MGas processed")]
        public static decimal Mgas { get; set; }

        [CounterMetric]
        [Description("Total number of transactions processed")]
        public static long Transactions { get; set; }

        [GaugeMetric]
        [Description("Total number of blocks processed")]
        public static long Blocks { get; set; }

        [CounterMetric]
        [Description("Total number of chain reorganizations")]
        public static long Reorganizations { get; set; }

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

        [GaugeMetric]
        [Description("Gas Used in processed blocks")]
        public static long GasUsed { get; set; }

        [GaugeMetric]
        [Description("Gas Limit for processed blocks")]
        public static long GasLimit { get; set; }

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
        public static long BlockchainHeight { get; set; }

        //EIP-2159: Common Prometheus Metrics Names for Clients
        [GaugeMetric]
        [Description("The estimated highest block available.")]
        [DataMember(Name = "ethereum_best_known_block_number")]
        public static long BestKnownBlockNumber { get; set; }

        [GaugeMetric]
        [Description("Number of invalid blocks.")]
        public static long BadBlocks;

        [GaugeMetric]
        [Description("Number of invalid blocks with extra data set to 'Nethermind'.")]
        public static long BadBlocksByNethermindNodes;

        [GaugeMetric]
        [Description("Hash of the last block.")]
        public static ulong First8BytesOfLastBlockHash { get; set; }

        /// <summary>
        /// Sets the last block hash. The value of the hash is truncated to the first 64 bits
        /// and converted to "long" because Prometheus does not support string values.
        /// </summary>
        /// <param name="hash">Hash of the block.</param>
        public static void SetFirst8BytesOfLastBlockHash(string hash)
        {
            // Remove leading "0x" if present
            hash = hash.StartsWith("0x") ? hash.Substring(2) : hash;
            // Ensure that the length of the hex string is at least 16 characters
            if (hash.Length < 16)
            {
                throw new ArgumentException("Insufficient hex string length");
            }
            // Take the first 16 characters of the hex string
            string first8BytesHex = hash.Substring(0, 16);
            // Convert the hexadecimal string to a long
            // First8BytesOfLastBlockHash = ulong.Parse(first8BytesHex, System.Globalization.NumberStyles.HexNumber);

            First8BytesOfLastBlockHash = Convert.ToUInt64(first8BytesHex, 16);
            
        }
    }
}
