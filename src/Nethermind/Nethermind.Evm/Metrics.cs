// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm;

public class Metrics
{
    [CounterMetric]
    [Description("Number of EXTCODESIZE opcodes executed.")]
    public static long ExtCodeSizeOpcode;

    [CounterMetric]
    [Description("Number of EXTCODESIZE ISZERO optimizations.")]
    public static long ExtCodeSizeOptimizedIsZero;

    [CounterMetric]
    [Description("Number of EXTCODESIZE GT optimizations.")]
    public static long ExtCodeSizeOptimizedGT;

    [CounterMetric]
    [Description("Number of EXTCODESIZE EQ optimizations.")]
    public static long ExtCodeSizeOptimizedEQ;

    [CounterMetric]
    [Description("Number of EVM exceptions thrown by contracts.")]
    public static long EvmExceptions { get; set; }

    [CounterMetric]
    [Description("Number of SELFDESTRUCT calls.")]
    public static long SelfDestructs { get; set; }

    [CounterMetric]
    [Description("Number of calls to other contracts.")]
    public static long Calls { get; set; }

    [CounterMetric]
    [Description("Number of SLOAD opcodes executed.")]
    public static long SloadOpcode { get; set; }

    [CounterMetric]
    [Description("Number of SSTORE opcodes executed.")]
    public static long SstoreOpcode { get; set; }

    [CounterMetric]
    [Description("Number of TLOAD opcodes executed.")]
    public static long TloadOpcode { get; set; }

    [CounterMetric]
    [Description("Number of TSTORE opcodes executed.")]
    public static long TstoreOpcode { get; set; }

    [CounterMetric]
    [Description("Number of MODEXP precompiles executed.")]
    public static long ModExpOpcode { get; set; }

    [CounterMetric]
    [Description("Number of BLOCKHASH opcodes executed.")]
    public static long BlockhashOpcode { get; set; }

    [CounterMetric]
    [Description("Number of BN254_MUL precompile calls.")]
    public static long Bn254MulPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BN254_ADD precompile calls.")]
    public static long Bn254AddPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of BN254_PAIRING precompile calls.")]
    public static long Bn254PairingPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of EC_RECOVERY precompile calls.")]
    public static long EcRecoverPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of MODEXP precompile calls.")]
    public static long ModExpPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of RIPEMD160 precompile calls.")]
    public static long Ripemd160Precompile { get; set; }

    [CounterMetric]
    [Description("Number of SHA256 precompile calls.")]
    public static long Sha256Precompile { get; set; }

    [CounterMetric]
    [Description("Number of Point Evaluation precompile calls.")]
    public static long PointEvaluationPrecompile { get; set; }

    [Description("Number of calls made to addresses without code.")]
    public static long EmptyCalls { get; set; }

    [Description("Number of contract create calls.")]
    public static long Creates { get; set; }

    public static long Transactions { get; set; }
    public static decimal AveGasPrice { get; set; }
    public static decimal MinGasPrice { get; set; } = decimal.MaxValue;
    public static decimal MaxGasPrice { get; set; }
    public static decimal EstMedianGasPrice { get; set; }

    public static long BlockTransactions { get; set; }
    public static decimal BlockAveGasPrice { get; set; }
    public static decimal BlockMinGasPrice { get; set; } = decimal.MaxValue;
    public static decimal BlockMaxGasPrice { get; set; }
    public static decimal BlockEstMedianGasPrice { get; set; }

    public static void ResetBlockStats()
    {
        BlockTransactions = 0;
        BlockAveGasPrice = 0m;
        BlockMaxGasPrice = 0m;
        BlockEstMedianGasPrice = 0m;
        BlockMinGasPrice = decimal.MaxValue;
    }
}
