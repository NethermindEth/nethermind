// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Evm
{
    public class Metrics
    {
        [Description("Number of EVM exceptions thrown by contracts.")]
        [CounterMetric]
        public static long EvmExceptions { get; set; }

        [Description("Number of SELFDESTRUCT calls.")]
        [CounterMetric]
        public static long SelfDestructs { get; set; }

        [Description("Number of calls to other contracts.")]
        [CounterMetric]
        public static long Calls { get; set; }

        [Description("Number of SLOAD opcodes executed.")]
        [CounterMetric]
        public static long SloadOpcode { get; set; }

        [Description("Number of SSTORE opcodes executed.")]
        [CounterMetric]
        public static long SstoreOpcode { get; set; }

        [Description("Number of TLOAD opcodes executed.")]
        [CounterMetric]
        public static long TloadOpcode { get; set; }

        [Description("Number of TSTORE opcodes executed.")]
        [CounterMetric]
        public static long TstoreOpcode { get; set; }

        [Description("Number of MODEXP precompiles executed.")]
        [CounterMetric]
        public static long ModExpOpcode { get; set; }

        [Description("Number of BLOCKHASH opcodes executed.")]
        [CounterMetric]
        public static long BlockhashOpcode { get; set; }

        [Description("Number of BN256_MUL precompile calls.")]
        [CounterMetric]
        public static long Bn256MulPrecompile { get; set; }

        [Description("Number of BN256_ADD precompile calls.")]
        [CounterMetric]
        public static long Bn256AddPrecompile { get; set; }

        [Description("Number of BN256_PAIRING precompile calls.")]
        [CounterMetric]
        public static long Bn256PairingPrecompile { get; set; }

        [Description("Number of EC_RECOVERY precompile calls.")]
        [CounterMetric]
        public static long EcRecoverPrecompile { get; set; }

        [Description("Number of MODEXP precompile calls.")]
        [CounterMetric]
        public static long ModExpPrecompile { get; set; }

        [Description("Number of RIPEMD160 precompile calls.")]
        [CounterMetric]
        public static long Ripemd160Precompile { get; set; }

        [Description("Number of SHA256 precompile calls.")]
        [CounterMetric]
        public static long Sha256Precompile { get; set; }

        [Description("Number of Point Evaluation precompile calls.")]
        [CounterMetric]
        public static long PointEvaluationPrecompile { get; set; }
    }
}
