// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Attributes;
using Nethermind.Core.Threading;

[assembly: InternalsVisibleTo("Nethermind.Consensus")]
[assembly: InternalsVisibleTo("Nethermind.State")]
[assembly: InternalsVisibleTo("Nethermind.Core.Test")]
[assembly: InternalsVisibleTo("Nethermind.Consensus.Test")]
[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

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
    [Description("Number of opcodes executed.")]
    public static long OpCodes => _sOpCodes.GetTotalValue();
    private static readonly ZeroContentionCounter _sOpCodes = new();
    [Description("Number of opcodes executed on thread.")]
    public static long ThreadLocalOpCodes => _sOpCodes.ThreadLocalValue;
    public static void IncrementOpCodes(int count) => _sOpCodes.Increment(count);

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

    // Consolidated execution metrics accumulator.
    private static readonly ThreadLocal<ExecutionMetricsAccumulator> _executionMetrics =
        new(static () => new ExecutionMetricsAccumulator(), trackAllValues: true);

    /// <summary>
    /// Per-thread accumulator for execution metrics. Access fields directly for low-overhead increments.
    /// </summary>
    internal static ExecutionMetricsAccumulator ThreadExecutionMetrics => _executionMetrics.Value!;

    private static long SumExecutionMetric(Func<ExecutionMetricsAccumulator, long> selector)
    {
        long total = 0;
        foreach (ExecutionMetricsAccumulator acc in _executionMetrics.Values)
            total += selector(acc);
        return total;
    }

    // State access metrics for cross-client execution metrics standardization
    [CounterMetric]
    [Description("Number of account reads during execution.")]
    public static long AccountReads => SumExecutionMetric(static a => a.AccountReads);
    [Description("Number of account reads on thread.")]
    internal static long ThreadLocalAccountReads => ThreadExecutionMetrics.AccountReads;

    [CounterMetric]
    [Description("Number of storage slot reads during execution.")]
    public static long StorageReads => SumExecutionMetric(static a => a.StorageReads);
    [Description("Number of storage slot reads on thread.")]
    internal static long ThreadLocalStorageReads => ThreadExecutionMetrics.StorageReads;

    [CounterMetric]
    [Description("Number of code reads during execution.")]
    public static long CodeReads => SumExecutionMetric(static a => a.CodeReads);
    [Description("Number of code reads on thread.")]
    internal static long ThreadLocalCodeReads => ThreadExecutionMetrics.CodeReads;

    [CounterMetric]
    [Description("Total bytes of code read during execution.")]
    public static long CodeBytesRead => SumExecutionMetric(static a => a.CodeBytesRead);
    [Description("Total bytes of code read on thread.")]
    internal static long ThreadLocalCodeBytesRead => ThreadExecutionMetrics.CodeBytesRead;

    [CounterMetric]
    [Description("Number of account writes during execution.")]
    public static long AccountWrites => SumExecutionMetric(static a => a.AccountWrites);
    [Description("Number of account writes on thread.")]
    internal static long ThreadLocalAccountWrites => ThreadExecutionMetrics.AccountWrites;

    [CounterMetric]
    [Description("Number of accounts deleted during execution.")]
    public static long AccountDeleted => SumExecutionMetric(static a => a.AccountDeleted);
    [Description("Number of accounts deleted on thread.")]
    internal static long ThreadLocalAccountDeleted => ThreadExecutionMetrics.AccountDeleted;

    [CounterMetric]
    [Description("Number of storage slot writes during execution.")]
    public static long StorageWrites => SumExecutionMetric(static a => a.StorageWrites);
    [Description("Number of storage slot writes on thread.")]
    internal static long ThreadLocalStorageWrites => ThreadExecutionMetrics.StorageWrites;

    [CounterMetric]
    [Description("Number of storage slots deleted during execution.")]
    public static long StorageDeleted => SumExecutionMetric(static a => a.StorageDeleted);
    [Description("Number of storage slots deleted on thread.")]
    internal static long ThreadLocalStorageDeleted => ThreadExecutionMetrics.StorageDeleted;

    [CounterMetric]
    [Description("Number of code writes during execution.")]
    public static long CodeWrites => SumExecutionMetric(static a => a.CodeWrites);
    [Description("Number of code writes on thread.")]
    internal static long ThreadLocalCodeWrites => ThreadExecutionMetrics.CodeWrites;

    [CounterMetric]
    [Description("Total bytes of code written during execution.")]
    public static long CodeBytesWritten => SumExecutionMetric(static a => a.CodeBytesWritten);
    [Description("Total bytes of code written on thread.")]
    internal static long ThreadLocalCodeBytesWritten => ThreadExecutionMetrics.CodeBytesWritten;

    // EIP-7702 delegation tracking for cross-client execution metrics standardization
    [CounterMetric]
    [Description("Number of EIP-7702 delegations set during execution.")]
    public static long Eip7702DelegationsSet => SumExecutionMetric(static a => a.Eip7702DelegationsSet);
    [Description("Number of EIP-7702 delegations set on thread.")]
    internal static long ThreadLocalEip7702DelegationsSet => ThreadExecutionMetrics.Eip7702DelegationsSet;

    [CounterMetric]
    [Description("Number of EIP-7702 delegations cleared during execution.")]
    public static long Eip7702DelegationsCleared => SumExecutionMetric(static a => a.Eip7702DelegationsCleared);
    [Description("Number of EIP-7702 delegations cleared on thread.")]
    internal static long ThreadLocalEip7702DelegationsCleared => ThreadExecutionMetrics.Eip7702DelegationsCleared;

    // Timing metrics for cross-client execution metrics standardization (in ticks for precision)
    [Description("Time spent reading state during execution (ticks).")]
    public static long StateReadTime => SumExecutionMetric(static a => a.StateReadTimeTicks);
    [Description("Time spent reading state on thread (ticks).")]
    internal static long ThreadLocalStateReadTime => ThreadExecutionMetrics.StateReadTimeTicks;

    [Description("Time spent on state hashing/merkleization (ticks).")]
    public static long StateHashTime => SumExecutionMetric(static a => a.StateHashTimeTicks);
    [Description("Time spent on state hashing on thread (ticks).")]
    internal static long ThreadLocalStateHashTime => ThreadExecutionMetrics.StateHashTimeTicks;

    [Description("Time spent committing state to storage (ticks).")]
    public static long CommitTime => SumExecutionMetric(static a => a.CommitTimeTicks);
    [Description("Time spent committing state on thread (ticks).")]
    internal static long ThreadLocalCommitTime => ThreadExecutionMetrics.CommitTimeTicks;

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

/// <summary>
/// Accumulates execution metrics per-thread. All fields are accessed directly for minimal overhead.
/// A single ThreadLocal instance holds this.
/// </summary>
internal sealed class ExecutionMetricsAccumulator
{
    // State reads
    public long AccountReads;
    public long StorageReads;
    public long CodeReads;
    public long CodeBytesRead;

    // State writes
    public long AccountWrites;
    public long AccountDeleted;
    public long StorageWrites;
    public long StorageDeleted;
    public long CodeWrites;
    public long CodeBytesWritten;

    // EIP-7702
    public long Eip7702DelegationsSet;
    public long Eip7702DelegationsCleared;

    // Timing (ticks)
    public long StateReadTimeTicks;
    public long StateHashTimeTicks;
    public long CommitTimeTicks;
}
