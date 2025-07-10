// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nethermind.Core.Threading;
using Nethermind.Core.Attributes;
using System.Threading;

[assembly: InternalsVisibleTo("Nethermind.Consensus")]

namespace Nethermind.Evm;

public class Metrics
{
    [CounterMetric]
    [Description("Number of EVM exceptions thrown by contracts.")]
    public static long EvmExceptions { get; set; }

    [CounterMetric]
    [Description("Number of SELFDESTRUCT calls.")]
    public static long SelfDestructs => _selfDestructs.GetTotalValue();
    private static readonly ZeroContentionCounter _selfDestructs = new();
    [Description("Number of calls to other contracts on thread.")]
    public static long ThreadLocalSelfDestructs => _selfDestructs.ThreadLocalValue;
    public static void IncrementSelfDestructs() => _selfDestructs.Increment();

    [CounterMetric]
    [Description("Number of calls to other contracts.")]
    public static long Calls => _calls.GetTotalValue();
    private static readonly ZeroContentionCounter _calls = new();
    [Description("Number of calls to other contracts on thread.")]
    public static long ThreadLocalCalls => _calls.ThreadLocalValue;
    public static void IncrementCalls() => _calls.Increment();

    [CounterMetric]
    [Description("Number of SLOAD opcodes executed.")]
    public static long SloadOpcode => _sLoadOpcode.GetTotalValue();
    private static readonly ZeroContentionCounter _sLoadOpcode = new();
    [Description("Number of SLOAD opcodes executed on thread.")]
    public static long ThreadLocalSLoadOpcode => _sLoadOpcode.ThreadLocalValue;
    public static void IncrementSLoadOpcode() => _sLoadOpcode.Increment();

    [CounterMetric]
    [Description("Number of SSTORE opcodes executed.")]
    public static long SstoreOpcode => _sStoreOpcode.GetTotalValue();
    private static readonly ZeroContentionCounter _sStoreOpcode = new();
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

    public static long _IlvmContractsAnalyzed;
    [Description("Number of contracts analyzed by ILVM")] // "ILVM" is an abbreviation for "Intermediate Language Virtual Machine
    public static long IlvmContractsAnalyzed => _IlvmContractsAnalyzed;
    public static void IncrementIlvmContractsAnalyzed()
    {
        Interlocked.Increment(ref _IlvmContractsAnalyzed);
    }

    [Description("Number of Ilemv precompile calls.")]
    public static long IlvmAotPrecompiledCalls { get; set; }

    public static long _IlvmAotQueueSize;
    [Description("Number of Ilemv precompile analysis queues.")]
    public static long IlvmAotQueueSize => _IlvmAotQueueSize;
    public static void IncrementIlvmAotQueueSize()
    {
        Interlocked.Increment(ref _IlvmAotQueueSize);
    }

    public static void DecrementIlvmAotQueueSize()
    {
        Interlocked.Decrement(ref _IlvmAotQueueSize);
    }

    public static long _IlvmAotQueueTasksCount;

    [Description("Number of Ilemv compiling tasks count.")]
    public static long IlvmAotQueueTasksCount => _IlvmAotQueueTasksCount;
    public static void IncrementIlvmAotQueueTasksCount()
    {
        Interlocked.Increment(ref _IlvmAotQueueTasksCount);
    }
    public static void DecrementIlvmAotQueueTasksCount()
    {
        Interlocked.Decrement(ref _IlvmAotQueueTasksCount);
    }


    public static long _IlvmAotContractsProcessed;

    [Description("Number of Ilemv compiling tasks count.")]
    public static long IlvmAotContractsProcessed => _IlvmAotContractsProcessed;
    public static void IncrementIlvmAotContractsProcessed()
    {
        Interlocked.Increment(ref _IlvmAotContractsProcessed);
    }

    public static long _IlvmCurrentlyProcessing;
    [Description("Number of Ilemv precompile contract currently being processing.")]
    public static long IlvmCurrentlyProcessing => _IlvmCurrentlyProcessing;
    public static void IncrementIlvmCurrentlyProcessing()
    {
        Interlocked.Increment(ref _IlvmCurrentlyProcessing);
    }
    public static void DecrementIlvmCurrentlyProcessing()
    {
        Interlocked.Decrement(ref _IlvmCurrentlyProcessing);
    }

    public static long _IlvmCurrentlyAnalysing;
    [Description("Number of Ilemv precompile contract currently being analyzed.")]
    public static long IlvmCurrentlyAnalysing => _IlvmCurrentlyAnalysing;
    public static void IncrementIlvmCurrentlyAnalysing()
    {
        Interlocked.Increment(ref _IlvmCurrentlyAnalysing);
    }
    public static void DecrementIlvmCurrentlyAnalysing()
    {
        Interlocked.Decrement(ref _IlvmCurrentlyAnalysing);
    }


    public static long _IlvmCurrentlyCompiling;
    [Description("Number of Ilemv precompile contract currently being compiled.")]
    public static long IlvmCurrentlyCompiling => _IlvmCurrentlyCompiling;
    public static void IncrementIlvmCurrentlyCompiling()
    {
        Interlocked.Increment(ref _IlvmCurrentlyCompiling);
    }
    public static void DecrementIlvmCurrentlyCompiling()
    {
        Interlocked.Decrement(ref _IlvmCurrentlyCompiling);
    }

    public static long _IlvmAotCacheTouched;
    [Description("Number of Ilemv precompile contract currently being retrieved from cache.")]
    public static long IlvmAotCacheTouched => _IlvmAotCacheTouched;
    public static void IncrementIlvmAotCacheTouched()
    {
        Interlocked.Increment(ref _IlvmAotCacheTouched);
    }


    public static long _IlvmAotDllSaved;
    [Description("Number of Ilemv precompile contract currently being retrieved from cache.")]
    public static long IlvmAotDllSaved => _IlvmAotCacheTouched;
    public static void IncrementIlvmAotDllSaved()
    {
        Interlocked.Increment(ref _IlvmAotCacheTouched);
    }



    [CounterMetric]
    [Description("Number of calls made to addresses without code.")]
    public static long EmptyCalls => _emptyCalls.GetTotalValue();
    private static readonly ZeroContentionCounter _emptyCalls = new();
    [Description("Number of calls made to addresses without code on thread.")]
    public static long ThreadLocalEmptyCalls => _emptyCalls.ThreadLocalValue;
    public static void IncrementEmptyCalls() => _emptyCalls.Increment();

    [CounterMetric]
    [Description("Number of contract create calls.")]
    public static long Creates => _creates.GetTotalValue();

    private static readonly ZeroContentionCounter _creates = new();
    [Description("Number of contract create calls on thread.")]
    public static long ThreadLocalCreates => _creates.ThreadLocalValue;
    public static void IncrementCreates() => _creates.Increment();

    [Description("Number of contracts' code analysed for jump destinations.")]
    public static long ContractsAnalysed => _contractsAnalysed.GetTotalValue();
    private static readonly ZeroContentionCounter _contractsAnalysed = new();
    [Description("Number of contracts' code analysed for jump destinations on thread.")]
    public static long ThreadLocalContractsAnalysed => _contractsAnalysed.ThreadLocalValue;
    public static void IncrementContractsAnalysed() => _contractsAnalysed.Increment();

    [GaugeMetric]
    [Description("The number of tasks currently scheduled in the background.")]
    public static long NumberOfBackgroundTasksScheduled { get; set; }

    private static long _totalBackgroundTasksQueued;
    [GaugeMetric]
    [Description("Total number of tasks queued for background execution.")]
    public static long TotalBackgroundTasksQueued => _totalBackgroundTasksQueued;
    public static void IncrementTotalBackgroundTasksQueued() => Interlocked.Increment(ref _totalBackgroundTasksQueued);

    internal static long BlockTransactions { get; set; }

    private static float _blockAveGasPrice;
    internal static float BlockAveGasPrice
    {
        get => _blockAveGasPrice;
        set
        {
            _blockAveGasPrice = value;
            if (value != 0)
            {
                GasPriceAve = value;
            }
        }
    }

    private static float _blockMinGasPrice = float.MaxValue;
    internal static float BlockMinGasPrice
    {
        get => _blockMinGasPrice;
        set
        {
            _blockMinGasPrice = value;
            if (_blockMinGasPrice != float.MaxValue)
            {
                GasPriceMin = value;
            }
        }
    }

    private static float _blockMaxGasPrice;
    internal static float BlockMaxGasPrice
    {
        get => _blockMaxGasPrice;
        set
        {
            _blockMaxGasPrice = value;
            if (value != 0)
            {
                GasPriceMax = value;
            }
        }
    }

    private static float _blockEstMedianGasPrice;
    internal static float BlockEstMedianGasPrice
    {
        get => _blockEstMedianGasPrice;
        set
        {
            _blockEstMedianGasPrice = value;
            if (value != 0)
            {
                GasPriceMedian = value;
            }
        }
    }

    [GaugeMetric]
    [Description("Minimum tx gas price in block")]
    public static float GasPriceMin { get; private set; }

    [GaugeMetric]
    [Description("Median tx gas price in block")]
    public static float GasPriceMedian { get; private set; }

    [GaugeMetric]
    [Description("Mean tx gas price in block")]
    public static float GasPriceAve { get; private set; }

    [GaugeMetric]
    [Description("Maximum tx gas price in block")]
    public static float GasPriceMax { get; private set; }

    public static void ResetBlockStats()
    {
        BlockTransactions = 0;
        BlockAveGasPrice = 0.0f;
        BlockMaxGasPrice = 0.0f;
        BlockEstMedianGasPrice = 0.0f;
        BlockMinGasPrice = float.MaxValue;
    }
}
