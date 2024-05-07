// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.CompilerServices;

using Nethermind.Core.Attributes;

[assembly: InternalsVisibleTo("Nethermind.Consensus")]

namespace Nethermind.Evm;

public class Metrics
{
    [CounterMetric]
    [Description("Number of EVM exceptions thrown by contracts.")]
    public static long EvmExceptions { get; set; }

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

    [Description("Number of TLOAD opcodes executed.")]
    public static long TloadOpcode { get; set; }

    [Description("Number of TSTORE opcodes executed.")]
    public static long TstoreOpcode { get; set; }

    [Description("Number of MCOPY opcodes executed.")]
    public static long MCopyOpcode { get; set; }

    [Description("Number of MODEXP precompiles executed.")]
    public static long ModExpOpcode { get; set; }

    [Description("Number of BLOCKHASH opcodes executed.")]
    public static long BlockhashOpcode { get; set; }

    [Description("Number of BN254_MUL precompile calls.")]
    public static long Bn254MulPrecompile { get; set; }

    [Description("Number of BN254_ADD precompile calls.")]
    public static long Bn254AddPrecompile { get; set; }

    [Description("Number of BN254_PAIRING precompile calls.")]
    public static long Bn254PairingPrecompile { get; set; }

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

    [CounterMetric]
    [Description("Number of calls made to addresses without code.")]
    public static long EmptyCalls { get; set; }

    [CounterMetric]
    [Description("Number of contract create calls.")]
    public static long Creates { get; set; }
    internal static long Transactions { get; set; }
    internal static float AveGasPrice { get; set; }
    internal static float MinGasPrice { get; set; } = float.MaxValue;
    internal static float MaxGasPrice { get; set; }
    internal static float EstMedianGasPrice { get; set; }

    internal static long BlockTransactions { get; set; }
    internal static float BlockAveGasPrice { get; set; }
    internal static float BlockMinGasPrice { get; set; } = float.MaxValue;
    internal static float BlockMaxGasPrice { get; set; }
    internal static float BlockEstMedianGasPrice { get; set; }

    public static void ResetBlockStats()
    {
        BlockTransactions = 0;
        BlockAveGasPrice = 0.0f;
        BlockMaxGasPrice = 0.0f;
        BlockEstMedianGasPrice = 0.0f;
        BlockMinGasPrice = float.MaxValue;
    }
}
