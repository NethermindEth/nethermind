// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Attributes;
using Nethermind.Core.Threading;

[assembly: InternalsVisibleTo("Nethermind.Consensus")]

namespace Nethermind.Evm;

public class Metrics
{
    private static bool IsBlockProcessingThread => ProcessingThread.IsBlockProcessingThread;

    [CounterMetric]
    [Description("Number of Code DB cache reads.")]
    public static long CodeDbCache => _mainCodeDbCache + _otherCodeDbCache;
    private static long _mainCodeDbCache;
    private static long _otherCodeDbCache;
    [Description("Number of Code DB cache reads on main processing thread.")]
    internal static long MainThreadCodeDbCache => _mainCodeDbCache;
    internal static void IncrementCodeDbCache()
    {
        if (IsBlockProcessingThread)
            _mainCodeDbCache++;
        else
            Interlocked.Increment(ref _otherCodeDbCache);
    }

    [CounterMetric]
    [Description("Number of EVM exceptions thrown by contracts.")]
    public static long EvmExceptions { get; set; }

    [CounterMetric]
    [Description("Number of opcodes executed.")]
    public static long OpCodes => _mainOpCodes + _otherOpCodes;
    private static long _mainOpCodes;
    private static long _otherOpCodes;
    [Description("Number of opcodes executed on main processing thread.")]
    public static long MainThreadOpCodes => _mainOpCodes;
    public static void IncrementOpCodes(int count)
    {
        if (IsBlockProcessingThread)
            _mainOpCodes += count;
        else
            Interlocked.Add(ref _otherOpCodes, count);
    }

    [CounterMetric]
    [Description("Number of SELFDESTRUCT calls.")]
    public static long SelfDestructs => _mainSelfDestructs + _otherSelfDestructs;
    private static long _mainSelfDestructs;
    private static long _otherSelfDestructs;
    [Description("Number of SELFDESTRUCT calls on main processing thread.")]
    public static long MainThreadSelfDestructs => _mainSelfDestructs;
    public static void IncrementSelfDestructs()
    {
        if (IsBlockProcessingThread)
            _mainSelfDestructs++;
        else
            Interlocked.Increment(ref _otherSelfDestructs);
    }

    [CounterMetric]
    [Description("Number of calls to other contracts.")]
    public static long Calls => _mainCalls + _otherCalls;
    private static long _mainCalls;
    private static long _otherCalls;
    [Description("Number of calls to other contracts on main processing thread.")]
    public static long MainThreadCalls => _mainCalls;
    public static void IncrementCalls()
    {
        if (IsBlockProcessingThread)
            _mainCalls++;
        else
            Interlocked.Increment(ref _otherCalls);
    }

    [CounterMetric]
    [Description("Number of SLOAD opcodes executed.")]
    public static long SloadOpcode => _mainSLoadOpcode + _otherSLoadOpcode;
    private static long _mainSLoadOpcode;
    private static long _otherSLoadOpcode;
    [Description("Number of SLOAD opcodes executed on main processing thread.")]
    public static long MainThreadSLoadOpcode => _mainSLoadOpcode;
    public static void IncrementSLoadOpcode()
    {
        if (IsBlockProcessingThread)
            _mainSLoadOpcode++;
        else
            Interlocked.Increment(ref _otherSLoadOpcode);
    }

    [CounterMetric]
    [Description("Number of SSTORE opcodes executed.")]
    public static long SstoreOpcode => _mainSStoreOpcode + _otherSStoreOpcode;
    private static long _mainSStoreOpcode;
    private static long _otherSStoreOpcode;
    [Description("Number of SSTORE opcodes executed on main processing thread.")]
    public static long MainThreadSStoreOpcode => _mainSStoreOpcode;
    public static void IncrementSStoreOpcode()
    {
        if (IsBlockProcessingThread)
            _mainSStoreOpcode++;
        else
            Interlocked.Increment(ref _otherSStoreOpcode);
    }

    [CounterMetric]
    [Description("Number of calls made to addresses without code.")]
    public static long EmptyCalls => _mainEmptyCalls + _otherEmptyCalls;
    private static long _mainEmptyCalls;
    private static long _otherEmptyCalls;
    [Description("Number of calls made to addresses without code on main processing thread.")]
    public static long MainThreadEmptyCalls => _mainEmptyCalls;
    public static void IncrementEmptyCalls()
    {
        if (IsBlockProcessingThread)
            _mainEmptyCalls++;
        else
            Interlocked.Increment(ref _otherEmptyCalls);
    }

    [CounterMetric]
    [Description("Number of contract create calls.")]
    public static long Creates => _mainCreates + _otherCreates;
    private static long _mainCreates;
    private static long _otherCreates;
    [Description("Number of contract create calls on main processing thread.")]
    public static long MainThreadCreates => _mainCreates;
    public static void IncrementCreates()
    {
        if (IsBlockProcessingThread)
            _mainCreates++;
        else
            Interlocked.Increment(ref _otherCreates);
    }

    [Description("Number of contracts' code analysed for jump destinations.")]
    public static long ContractsAnalysed => _mainContractsAnalysed + _otherContractsAnalysed;
    private static long _mainContractsAnalysed;
    private static long _otherContractsAnalysed;
    [Description("Number of contracts' code analysed for jump destinations on main processing thread.")]
    public static long MainThreadContractsAnalysed => _mainContractsAnalysed;
    public static void IncrementContractsAnalysed()
    {
        if (IsBlockProcessingThread)
            _mainContractsAnalysed++;
        else
            Interlocked.Increment(ref _otherContractsAnalysed);
    }

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

    /// <summary>
    /// Gets block gas price data for external access. Returns (min, estMedian, ave, max).
    /// Returns null if no gas data available (min is float.MaxValue).
    /// </summary>
    public static (float Min, float EstMedian, float Ave, float Max)? GetBlockGasPrices()
    {
        if (_blockMinGasPrice == float.MaxValue)
            return null;

        return (_blockMinGasPrice, _blockEstMedianGasPrice, _blockAveGasPrice, _blockMaxGasPrice);
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
