// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nethermind.Core.Threading;
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
    public static long Calls => _calls.GetTotalValue();
    private static ZeroContentionCounter _calls = new();
    [Description("Number of calls to other contracts on thread.")]
    public static long ThreadLocalCalls => _calls.ThreadLocalValue;
    public static void IncrementCalls() => _calls.Increment();

    [CounterMetric]
    [Description("Number of SLOAD opcodes executed.")]
    public static long SloadOpcode => _sLoadOpcode.GetTotalValue();
    private static ZeroContentionCounter _sLoadOpcode = new();
    [Description("Number of SLOAD opcodes executed on thread.")]
    public static long ThreadLocalSLoadOpcode => _sLoadOpcode.ThreadLocalValue;
    public static void IncrementSLoadOpcode() => _sLoadOpcode.Increment();

    [CounterMetric]
    [Description("Number of SSTORE opcodes executed.")]
    public static long SstoreOpcode => _sStoreOpcode.GetTotalValue();
    private static ZeroContentionCounter _sStoreOpcode = new();
    [Description("Number of SSTORE opcodes executed on thread.")]
    public static long ThreadLocalSStoreOpcode => _sStoreOpcode.ThreadLocalValue;
    public static void IncrementSStoreOpcode() => _sStoreOpcode.Increment();

    [Description("Number of TLOAD opcodes executed.")]
    public static long TloadOpcode { get; set; }

    [Description("Number of TSTORE opcodes executed.")]
    public static long TstoreOpcode { get; set; }

    [Description("Number of MCOPY opcodes executed.")]
    public static long MCopyOpcode { get; set; }

    [Description("Number of EXP opcodes executed.")]
    public static long ExpOpcode { get; set; }

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

    [Description("Number of Secp256r1 precompile calls.")]
    public static long Secp256r1Precompile { get; set; }

    [Description("Number of Point Evaluation precompile calls.")]
    public static long PointEvaluationPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of calls made to addresses without code.")]
    public static long EmptyCalls => _emptyCalls.GetTotalValue();
    private static ZeroContentionCounter _emptyCalls = new();
    [Description("Number of calls made to addresses without code on thread.")]
    public static long ThreadLocalEmptyCalls => _emptyCalls.ThreadLocalValue;
    public static void IncrementEmptyCalls() => _emptyCalls.Increment();

    [CounterMetric]
    [Description("Number of contract create calls.")]
    public static long Creates => _creates.GetTotalValue();

    private static ZeroContentionCounter _creates = new();
    [Description("Number of contract create calls on thread.")]
    public static long ThreadLocalCreates => _creates.ThreadLocalValue;
    public static void IncrementCreates() => _creates.Increment();

    [Description("Number of contracts' code analysed for jump destinations.")]
    public static long ContractsAnalysed => _contractsAnalysed.GetTotalValue();
    private static ZeroContentionCounter _contractsAnalysed = new();
    [Description("Number of contracts' code analysed for jump destinations on thread.")]
    public static long ThreadLocalContractsAnalysed => _contractsAnalysed.ThreadLocalValue;
    public static void IncrementContractsAnalysed() => _contractsAnalysed.Increment();

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
