// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nethermind.Core.Threading;
using Nethermind.Core.Attributes;
using System.Threading;

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

    // State access metrics for cross-client execution metrics standardization
    [CounterMetric]
    [Description("Number of account reads during execution.")]
    public static long AccountReads => _accountReads.GetTotalValue();
    private static readonly ZeroContentionCounter _accountReads = new();
    [Description("Number of account reads on thread.")]
    internal static long ThreadLocalAccountReads => _accountReads.ThreadLocalValue;
    internal static void IncrementAccountReads() => _accountReads.Increment();

    [CounterMetric]
    [Description("Number of storage slot reads during execution.")]
    public static long StorageReads => _storageReads.GetTotalValue();
    private static readonly ZeroContentionCounter _storageReads = new();
    [Description("Number of storage slot reads on thread.")]
    internal static long ThreadLocalStorageReads => _storageReads.ThreadLocalValue;
    internal static void IncrementStorageReads() => _storageReads.Increment();

    [CounterMetric]
    [Description("Number of code reads during execution.")]
    public static long CodeReads => _codeReads.GetTotalValue();
    private static readonly ZeroContentionCounter _codeReads = new();
    [Description("Number of code reads on thread.")]
    internal static long ThreadLocalCodeReads => _codeReads.ThreadLocalValue;
    internal static void IncrementCodeReads() => _codeReads.Increment();

    [CounterMetric]
    [Description("Total bytes of code read during execution.")]
    public static long CodeBytesRead => _codeBytesRead.GetTotalValue();
    private static readonly ZeroContentionCounter _codeBytesRead = new();
    [Description("Total bytes of code read on thread.")]
    internal static long ThreadLocalCodeBytesRead => _codeBytesRead.ThreadLocalValue;
    internal static void IncrementCodeBytesRead(int bytes) => _codeBytesRead.Increment(bytes);

    [CounterMetric]
    [Description("Number of account writes during execution.")]
    public static long AccountWrites => _accountWrites.GetTotalValue();
    private static readonly ZeroContentionCounter _accountWrites = new();
    [Description("Number of account writes on thread.")]
    internal static long ThreadLocalAccountWrites => _accountWrites.ThreadLocalValue;
    internal static void IncrementAccountWrites() => _accountWrites.Increment();

    [CounterMetric]
    [Description("Number of accounts deleted during execution.")]
    public static long AccountDeleted => _accountDeleted.GetTotalValue();
    private static readonly ZeroContentionCounter _accountDeleted = new();
    [Description("Number of accounts deleted on thread.")]
    internal static long ThreadLocalAccountDeleted => _accountDeleted.ThreadLocalValue;
    internal static void IncrementAccountDeleted() => _accountDeleted.Increment();

    [CounterMetric]
    [Description("Number of storage slot writes during execution.")]
    public static long StorageWrites => _storageWrites.GetTotalValue();
    private static readonly ZeroContentionCounter _storageWrites = new();
    [Description("Number of storage slot writes on thread.")]
    internal static long ThreadLocalStorageWrites => _storageWrites.ThreadLocalValue;
    internal static void IncrementStorageWrites() => _storageWrites.Increment();

    [CounterMetric]
    [Description("Number of storage slots deleted during execution.")]
    public static long StorageDeleted => _storageDeleted.GetTotalValue();
    private static readonly ZeroContentionCounter _storageDeleted = new();
    [Description("Number of storage slots deleted on thread.")]
    internal static long ThreadLocalStorageDeleted => _storageDeleted.ThreadLocalValue;
    internal static void IncrementStorageDeleted() => _storageDeleted.Increment();

    // Code write metrics for cross-client execution metrics standardization
    [CounterMetric]
    [Description("Number of code writes during execution.")]
    public static long CodeWrites => _codeWrites.GetTotalValue();
    private static readonly ZeroContentionCounter _codeWrites = new();
    [Description("Number of code writes on thread.")]
    internal static long ThreadLocalCodeWrites => _codeWrites.ThreadLocalValue;
    internal static void IncrementCodeWrites() => _codeWrites.Increment();

    [CounterMetric]
    [Description("Total bytes of code written during execution.")]
    public static long CodeBytesWritten => _codeBytesWritten.GetTotalValue();
    private static readonly ZeroContentionCounter _codeBytesWritten = new();
    [Description("Total bytes of code written on thread.")]
    internal static long ThreadLocalCodeBytesWritten => _codeBytesWritten.ThreadLocalValue;
    internal static void IncrementCodeBytesWritten(int bytes) => _codeBytesWritten.Increment(bytes);

    // EIP-7702 delegation tracking for cross-client execution metrics standardization
    [CounterMetric]
    [Description("Number of EIP-7702 delegations set during execution.")]
    public static long Eip7702DelegationsSet => _eip7702DelegationsSet.GetTotalValue();
    private static readonly ZeroContentionCounter _eip7702DelegationsSet = new();
    [Description("Number of EIP-7702 delegations set on thread.")]
    internal static long ThreadLocalEip7702DelegationsSet => _eip7702DelegationsSet.ThreadLocalValue;
    internal static void IncrementEip7702DelegationsSet() => _eip7702DelegationsSet.Increment();

    [CounterMetric]
    [Description("Number of EIP-7702 delegations cleared during execution.")]
    public static long Eip7702DelegationsCleared => _eip7702DelegationsCleared.GetTotalValue();
    private static readonly ZeroContentionCounter _eip7702DelegationsCleared = new();
    [Description("Number of EIP-7702 delegations cleared on thread.")]
    internal static long ThreadLocalEip7702DelegationsCleared => _eip7702DelegationsCleared.ThreadLocalValue;
    internal static void IncrementEip7702DelegationsCleared() => _eip7702DelegationsCleared.Increment();

    // Timing metrics for cross-client execution metrics standardization (in ticks for precision)
    [Description("Time spent reading state during execution (ticks).")]
    public static long StateReadTime => _stateReadTime.GetTotalValue();
    private static readonly ZeroContentionCounter _stateReadTime = new();
    [Description("Time spent reading state on thread (ticks).")]
    internal static long ThreadLocalStateReadTime => _stateReadTime.ThreadLocalValue;
    internal static void AddStateReadTime(long ticks) => _stateReadTime.Increment(ticks);

    [Description("Time spent on state hashing/merkleization (ticks).")]
    public static long StateHashTime => _stateHashTime.GetTotalValue();
    private static readonly ZeroContentionCounter _stateHashTime = new();
    [Description("Time spent on state hashing on thread (ticks).")]
    internal static long ThreadLocalStateHashTime => _stateHashTime.ThreadLocalValue;
    internal static void AddStateHashTime(long ticks) => _stateHashTime.Increment(ticks);

    [Description("Time spent committing state to storage (ticks).")]
    public static long CommitTime => _commitTime.GetTotalValue();
    private static readonly ZeroContentionCounter _commitTime = new();
    [Description("Time spent committing state on thread (ticks).")]
    internal static long ThreadLocalCommitTime => _commitTime.ThreadLocalValue;
    internal static void AddCommitTime(long ticks) => _commitTime.Increment(ticks);

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
