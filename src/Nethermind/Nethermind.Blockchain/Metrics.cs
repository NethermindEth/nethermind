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
        [Description("State root calculation time")]
        public static double StateMerkleizationTime { get; set; }

        [DetailedMetric]
        [ExponentialPowerHistogramMetric(Start = 10, Factor = 1.2, Count = 30)]
        [Description("Histogram of block MGas per second")]
        public static IMetricObserver BlockMGasPerSec { get; set; } = new NoopMetricObserver();

        [DetailedMetric]
        [ExponentialPowerHistogramMetric(Start = 100, Factor = 1.25, Count = 50)]
        [Description("Histogram of block prorcessing time")]
        public static IMetricObserver BlockProcessingTimeMicros { get; set; } = new NoopMetricObserver();

        // Precompiles

        [Description("Number of BN254_MUL precompile calls.")]
        public static long Bn254MulPrecompile { get; set; }

        [Description("Number of BN254_ADD precompile calls.")]
        public static long Bn254AddPrecompile { get; set; }

        [Description("Number of BN254_PAIRING precompile calls.")]
        public static long Bn254PairingPrecompile { get; set; }

        [Description("Number of BLS12_G1ADD precompile calls.")]
        public static long BlsG1AddPrecompile { get; set; }

        [Description("Number of BLS12_G1MUL precompile calls.")]
        public static long BlsG1MulPrecompile { get; set; }

        [Description("Number of BLS12_G1MSM precompile calls.")]
        public static long BlsG1MSMPrecompile { get; set; }

        [Description("Number of BLS12_G2ADD precompile calls.")]
        public static long BlsG2AddPrecompile { get; set; }

        [Description("Number of BLS12_G2MUL precompile calls.")]
        public static long BlsG2MulPrecompile { get; set; }

        [Description("Number of BLS12_G2MSM precompile calls.")]
        public static long BlsG2MSMPrecompile { get; set; }

        [Description("Number of BLS12_PAIRING_CHECK precompile calls.")]
        public static long BlsPairingCheckPrecompile { get; set; }

        [Description("Number of BLS12_MAP_FP_TO_G1 precompile calls.")]
        public static long BlsMapFpToG1Precompile { get; set; }

        [Description("Number of BLS12_MAP_FP2_TO_G2 precompile calls.")]
        public static long BlsMapFp2ToG2Precompile { get; set; }

        [Description("Number of EC_RECOVERY precompile calls.")]
        public static long EcRecoverPrecompile { get; set; }

        [Description("Number of MODEXP precompile calls.")]
        public static long ModExpPrecompile { get; set; }

        [Description("Number of RIPEMD160 precompile calls.")]
        public static long Ripemd160Precompile { get; set; }

        [Description("Number of SHA256 precompile calls.")]
        public static long Sha256Precompile { get; set; }

        [Description("Number of Secp256r1 precompile calls.")]
        public static long Secp256r1Precompile { get; set; }

        [Description("Number of Point Evaluation precompile calls.")]
        public static long PointEvaluationPrecompile { get; set; }
    }
