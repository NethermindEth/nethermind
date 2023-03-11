// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.Evm
{
    public class Metrics
    {
        [Description("Number of EVM exceptions thrown by contracts.")]
        public static long EvmExceptions { get; set; }

        [Description("Number of SELFDESTRUCT calls.")]
        public static long SelfDestructs { get; set; }

        [Description("Number of calls to other contracts.")]
        public static long Calls { get; set; }

        [Description("Number of SLOAD opcodes executed.")]
        public static long SloadOpcode { get; set; }

        [Description("Number of SSTORE opcodes executed.")]
        public static long SstoreOpcode { get; set; }

        [Description("Number of TLOAD opcodes executed.")]
        public static long TloadOpcode { get; set; }

        [Description("Number of TSTORE opcodes executed.")]
        public static long TstoreOpcode { get; set; }

        [Description("Number of MODEXP precompiles executed.")]
        public static long ModExpOpcode { get; set; }

        [Description("Number of BLOCKHASH opcodes executed.")]
        public static long BlockhashOpcode { get; set; }

        [Description("Number of BN256_MUL precompile calls.")]
        public static long Bn256MulPrecompile { get; set; }

        [Description("Number of BN256_ADD precompile calls.")]
        public static long Bn256AddPrecompile { get; set; }

        [Description("Number of BN256_PAIRING precompile calls.")]
        public static long Bn256PairingPrecompile { get; set; }

        [Description("Number of EC_RECOVERY precompile calls.")]
        public static long EcRecoverPrecompile { get; set; }

        [Description("Number of MODEXP precompile calls.")]
        public static long ModExpPrecompile { get; set; }

        [Description("Number of RIPEMD160 precompile calls.")]
        public static long Ripemd160Precompile { get; set; }

        [Description("Number of SHA256 precompile calls.")]
        public static long Sha256Precompile { get; set; }

        [Description("Number of Point Evaluation precompile calls.")]
        public static long PointEvaluationPrecompile { get; set; }
    }
}
