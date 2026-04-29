// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    private static bool IsBlockProcessingThread => ProcessingThread.IsBlockProcessingThread;

    [CounterMetric]
    [Description("Number of Code DB cache reads.")]
    public static long CodeDbCache => _mainCodeDbCache + _otherCodeDbCache;
    private static long _mainCodeDbCache;
    private static long _otherCodeDbCache;
    [Description("Number of Code DB cache reads on main processing thread.")]
    public static long MainThreadCodeDbCache => _mainCodeDbCache;
    internal static void IncrementCodeDbCache() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainCodeDbCache : ref _otherCodeDbCache);
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
    public static void IncrementOpCodes(int count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainOpCodes : ref _otherOpCodes, count);

    [CounterMetric]
    [Description("Number of SELFDESTRUCT calls.")]
    public static long SelfDestructs => _mainSelfDestructs + _otherSelfDestructs;
    private static long _mainSelfDestructs;
    private static long _otherSelfDestructs;
    [Description("Number of SELFDESTRUCT calls on main processing thread.")]
    public static long MainThreadSelfDestructs => _mainSelfDestructs;
    public static void IncrementSelfDestructs() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainSelfDestructs : ref _otherSelfDestructs);

    [CounterMetric]
    [Description("Number of calls to other contracts.")]
    public static long Calls => _mainCalls + _otherCalls;
    private static long _mainCalls;
    private static long _otherCalls;
    [Description("Number of calls to other contracts on main processing thread.")]
    public static long MainThreadCalls => _mainCalls;
    public static void IncrementCalls() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainCalls : ref _otherCalls);

    [CounterMetric]
    [Description("Number of SLOAD opcodes executed.")]
    public static long SloadOpcode => _mainSLoadOpcode + _otherSLoadOpcode;
    private static long _mainSLoadOpcode;
    private static long _otherSLoadOpcode;
    [Description("Number of SLOAD opcodes executed on main processing thread.")]
    public static long MainThreadSLoadOpcode => _mainSLoadOpcode;
    public static void IncrementSLoadOpcode() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainSLoadOpcode : ref _otherSLoadOpcode);

    [CounterMetric]
    [Description("Number of SSTORE opcodes executed.")]
    public static long SstoreOpcode => _mainSStoreOpcode + _otherSStoreOpcode;
    private static long _mainSStoreOpcode;
    private static long _otherSStoreOpcode;
    [Description("Number of SSTORE opcodes executed on main processing thread.")]
    public static long MainThreadSStoreOpcode => _mainSStoreOpcode;
    public static void IncrementSStoreOpcode() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainSStoreOpcode : ref _otherSStoreOpcode);

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
    public static long EmptyCalls => _mainEmptyCalls + _otherEmptyCalls;
    private static long _mainEmptyCalls;
    private static long _otherEmptyCalls;
    [Description("Number of calls made to addresses without code on main processing thread.")]
    public static long MainThreadEmptyCalls => _mainEmptyCalls;
    public static void IncrementEmptyCalls() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainEmptyCalls : ref _otherEmptyCalls);

    [CounterMetric]
    [Description("Number of contract create calls.")]
    public static long Creates => _mainCreates + _otherCreates;
    private static long _mainCreates;
    private static long _otherCreates;
    [Description("Number of contract create calls on main processing thread.")]
    public static long MainThreadCreates => _mainCreates;
    public static void IncrementCreates() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainCreates : ref _otherCreates);

    [Description("Number of contracts' code analysed for jump destinations.")]
    public static long ContractsAnalysed => _mainContractsAnalysed + _otherContractsAnalysed;
    private static long _mainContractsAnalysed;
    private static long _otherContractsAnalysed;
    [Description("Number of contracts' code analysed for jump destinations on main processing thread.")]
    public static long MainThreadContractsAnalysed => _mainContractsAnalysed;
    public static void IncrementContractsAnalysed() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainContractsAnalysed : ref _otherContractsAnalysed);

    // Cross-client execution metrics — same ZeroContentionCounter pattern as existing metrics.
    // Each counter is a single ThreadLocal<BoxedLong>; Increment() is just _threadLocal.Value!._value += 1.

    [CounterMetric]
    [Description("Number of account reads during execution.")]
    public static long AccountReads => _accountReads.GetTotalValue();
    private static readonly ZeroContentionCounter _accountReads = new();
    internal static long ThreadLocalAccountReads => _accountReads.ThreadLocalValue;
    internal static void IncrementAccountReads() => _accountReads.Increment();

    [CounterMetric]
    [Description("Number of storage slot reads during execution.")]
    public static long StorageReads => _storageReads.GetTotalValue();
    private static readonly ZeroContentionCounter _storageReads = new();
    internal static long ThreadLocalStorageReads => _storageReads.ThreadLocalValue;
    internal static void IncrementStorageReads() => _storageReads.Increment();

    [CounterMetric]
    [Description("Number of code reads during execution.")]
    public static long CodeReads => _codeReads.GetTotalValue();
    private static readonly ZeroContentionCounter _codeReads = new();
    internal static long ThreadLocalCodeReads => _codeReads.ThreadLocalValue;
    internal static void IncrementCodeReads() => _codeReads.Increment();

    [CounterMetric]
    [Description("Total bytes of code read during execution.")]
    public static long CodeBytesRead => _codeBytesRead.GetTotalValue();
    private static readonly ZeroContentionCounter _codeBytesRead = new();
    internal static long ThreadLocalCodeBytesRead => _codeBytesRead.ThreadLocalValue;
    internal static void IncrementCodeBytesRead(int bytes) => _codeBytesRead.Increment(bytes);

    [CounterMetric]
    [Description("Number of account writes during execution.")]
    public static long AccountWrites => _accountWrites.GetTotalValue();
    private static readonly ZeroContentionCounter _accountWrites = new();
    internal static long ThreadLocalAccountWrites => _accountWrites.ThreadLocalValue;
    internal static void IncrementAccountWrites() => _accountWrites.Increment();

    [CounterMetric]
    [Description("Number of accounts deleted during execution.")]
    public static long AccountDeleted => _accountDeleted.GetTotalValue();
    private static readonly ZeroContentionCounter _accountDeleted = new();
    internal static long ThreadLocalAccountDeleted => _accountDeleted.ThreadLocalValue;
    internal static void IncrementAccountDeleted() => _accountDeleted.Increment();

    [CounterMetric]
    [Description("Number of storage slot writes during execution.")]
    public static long StorageWrites => _storageWrites.GetTotalValue();
    private static readonly ZeroContentionCounter _storageWrites = new();
    internal static long ThreadLocalStorageWrites => _storageWrites.ThreadLocalValue;
    internal static void IncrementStorageWrites() => _storageWrites.Increment();

    [CounterMetric]
    [Description("Number of storage slots deleted during execution.")]
    public static long StorageDeleted => _storageDeleted.GetTotalValue();
    private static readonly ZeroContentionCounter _storageDeleted = new();
    internal static long ThreadLocalStorageDeleted => _storageDeleted.ThreadLocalValue;
    internal static void IncrementStorageDeleted() => _storageDeleted.Increment();

    [CounterMetric]
    [Description("Number of code writes during execution.")]
    public static long CodeWrites => _codeWrites.GetTotalValue();
    private static readonly ZeroContentionCounter _codeWrites = new();
    internal static long ThreadLocalCodeWrites => _codeWrites.ThreadLocalValue;
    internal static void IncrementCodeWrites() => _codeWrites.Increment();

    [CounterMetric]
    [Description("Total bytes of code written during execution.")]
    public static long CodeBytesWritten => _codeBytesWritten.GetTotalValue();
    private static readonly ZeroContentionCounter _codeBytesWritten = new();
    internal static long ThreadLocalCodeBytesWritten => _codeBytesWritten.ThreadLocalValue;
    internal static void IncrementCodeBytesWritten(int bytes) => _codeBytesWritten.Increment(bytes);

    [CounterMetric]
    [Description("Number of EIP-7702 delegations set during execution.")]
    public static long Eip7702DelegationsSet => _eip7702DelegationsSet.GetTotalValue();
    private static readonly ZeroContentionCounter _eip7702DelegationsSet = new();
    internal static long ThreadLocalEip7702DelegationsSet => _eip7702DelegationsSet.ThreadLocalValue;
    internal static void IncrementEip7702DelegationsSet() => _eip7702DelegationsSet.Increment();

    [CounterMetric]
    [Description("Number of EIP-7702 delegations cleared during execution.")]
    public static long Eip7702DelegationsCleared => _eip7702DelegationsCleared.GetTotalValue();
    private static readonly ZeroContentionCounter _eip7702DelegationsCleared = new();
    internal static long ThreadLocalEip7702DelegationsCleared => _eip7702DelegationsCleared.ThreadLocalValue;
    internal static void IncrementEip7702DelegationsCleared() => _eip7702DelegationsCleared.Increment();

    // Timing metrics — accumulated via ZeroContentionCounter(long) in coarse-grained paths only
    // (WorldStateMetricsDecorator, BlockProcessor — NOT per-read hot paths).
    [Description("Time spent on state hashing/merkleization (ticks). Sum of storage merkle + state root.")]
    public static long StateHashTime => _stateHashTime.GetTotalValue();
    private static readonly ZeroContentionCounter _stateHashTime = new();
    internal static long ThreadLocalStateHashTime => _stateHashTime.ThreadLocalValue;
    internal static void IncrementStateHashTime(long ticks) => _stateHashTime.Increment(ticks);

    [Description("Time spent committing state to storage (ticks).")]
    public static long CommitTime => _commitTime.GetTotalValue();
    private static readonly ZeroContentionCounter _commitTime = new();
    internal static long ThreadLocalCommitTime => _commitTime.ThreadLocalValue;
    internal static void IncrementCommitTime(long ticks) => _commitTime.Increment(ticks);

    [Description("Time spent on storage trie merkleization — Commit(commitRoots: true) (ticks).")]
    public static long StorageMerkleTime => _storageMerkleTime.GetTotalValue();
    private static readonly ZeroContentionCounter _storageMerkleTime = new();
    internal static long ThreadLocalStorageMerkleTime => _storageMerkleTime.ThreadLocalValue;
    internal static void IncrementStorageMerkleTime(long ticks) => _storageMerkleTime.Increment(ticks);

    [Description("Time spent on state root recalculation + commit tree (ticks).")]
    public static long StateRootTime => _stateRootTime.GetTotalValue();
    private static readonly ZeroContentionCounter _stateRootTime = new();
    internal static long ThreadLocalStateRootTime => _stateRootTime.ThreadLocalValue;
    internal static void IncrementStateRootTime(long ticks) => _stateRootTime.Increment(ticks);

    [Description("Time spent calculating bloom filters (ticks).")]
    public static long BloomsTime => _bloomsTime.GetTotalValue();
    private static readonly ZeroContentionCounter _bloomsTime = new();
    internal static long ThreadLocalBloomsTime => _bloomsTime.ThreadLocalValue;
    internal static void IncrementBloomsTime(long ticks) => _bloomsTime.Increment(ticks);

    [Description("Time spent calculating receipts root (ticks).")]
    public static long ReceiptsRootTime => _receiptsRootTime.GetTotalValue();
    private static readonly ZeroContentionCounter _receiptsRootTime = new();
    internal static long ThreadLocalReceiptsRootTime => _receiptsRootTime.ThreadLocalValue;
    internal static void IncrementReceiptsRootTime(long ticks) => _receiptsRootTime.Increment(ticks);

    [GaugeMetric]
    [Description("The number of tasks currently scheduled in the background.")]
    public static long NumberOfBackgroundTasksScheduled { get; set; }

    private static long _totalBackgroundTasksQueued;
    [GaugeMetric]
    [Description("Total number of tasks queued for background execution.")]
    public static long TotalBackgroundTasksQueued => _totalBackgroundTasksQueued;
    public static void IncrementTotalBackgroundTasksQueued() => Interlocked.Increment(ref _totalBackgroundTasksQueued);

    private static long _totalBackgroundTasksDropped;
    [GaugeMetric]
    [Description("Total number of background tasks dropped because queue was full.")]
    public static long TotalBackgroundTasksDropped => _totalBackgroundTasksDropped;
    public static void IncrementTotalBackgroundTasksDropped() => Interlocked.Increment(ref _totalBackgroundTasksDropped);

    private static long _totalBackgroundTasksExecuted;
    [GaugeMetric]
    [Description("Total number of background tasks executed.")]
    public static long TotalBackgroundTasksExecuted => _totalBackgroundTasksExecuted;
    public static void IncrementTotalBackgroundTasksExecuted() => Interlocked.Increment(ref _totalBackgroundTasksExecuted);

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
