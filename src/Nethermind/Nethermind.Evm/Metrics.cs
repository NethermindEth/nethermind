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
    [Description("Number of Code DB cache reads.")]
    public static long CodeDbCache => _codeDbCache.GetTotalValue();
    private static readonly ZeroContentionCounter _codeDbCache = new();
    [Description("Number of Code DB cache reads on thread.")]
    internal static long ThreadLocalCodeDbCache => _codeDbCache.ThreadLocalValue;
    internal static void IncrementCodeDbCache() => _codeDbCache.Increment();
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
    [Description("The number of tasks scheduled in the background.")]
    public static long NumberOfBackgroundTasksScheduled { get; set; }

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
