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

    [CounterMetric]
    [Description("Number of account reads during execution.")]
    public static long AccountReads => _mainAccountReads + _otherAccountReads;
    private static long _mainAccountReads;
    private static long _otherAccountReads;
    // Exposed for ProcessingStats so block-level deltas exclude background prewarmer activity.
    internal static long MainThreadAccountReads => _mainAccountReads;
    internal static void IncrementAccountReads() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainAccountReads : ref _otherAccountReads);

    [CounterMetric]
    [Description("Number of storage slot reads during execution.")]
    public static long StorageReads => _mainStorageReads + _otherStorageReads;
    private static long _mainStorageReads;
    private static long _otherStorageReads;
    internal static long MainThreadStorageReads => _mainStorageReads;
    internal static void IncrementStorageReads() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainStorageReads : ref _otherStorageReads);

    [CounterMetric]
    [Description("Number of code reads during execution.")]
    public static long CodeReads => _mainCodeReads + _otherCodeReads;
    private static long _mainCodeReads;
    private static long _otherCodeReads;
    internal static long MainThreadCodeReads => _mainCodeReads;
    internal static void IncrementCodeReads() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainCodeReads : ref _otherCodeReads);

    [CounterMetric]
    [Description("Total bytes of code read during execution.")]
    public static long CodeBytesRead => _mainCodeBytesRead + _otherCodeBytesRead;
    private static long _mainCodeBytesRead;
    private static long _otherCodeBytesRead;
    internal static long MainThreadCodeBytesRead => _mainCodeBytesRead;
    internal static void IncrementCodeBytesRead(int bytes) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainCodeBytesRead : ref _otherCodeBytesRead, bytes);

    [CounterMetric]
    [Description("Number of account writes during execution.")]
    public static long AccountWrites => _mainAccountWrites + _otherAccountWrites;
    private static long _mainAccountWrites;
    private static long _otherAccountWrites;
    internal static long MainThreadAccountWrites => _mainAccountWrites;
    internal static void IncrementAccountWrites() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainAccountWrites : ref _otherAccountWrites);

    [CounterMetric]
    [Description("Number of accounts deleted during execution.")]
    public static long AccountDeleted => _mainAccountDeleted + _otherAccountDeleted;
    private static long _mainAccountDeleted;
    private static long _otherAccountDeleted;
    internal static long MainThreadAccountDeleted => _mainAccountDeleted;
    internal static void IncrementAccountDeleted() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainAccountDeleted : ref _otherAccountDeleted);

    [CounterMetric]
    [Description("Number of storage slot writes during execution.")]
    public static long StorageWrites => _mainStorageWrites + _otherStorageWrites;
    private static long _mainStorageWrites;
    private static long _otherStorageWrites;
    internal static long MainThreadStorageWrites => _mainStorageWrites;
    internal static void IncrementStorageWrites() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainStorageWrites : ref _otherStorageWrites);

    [CounterMetric]
    [Description("Number of storage slots deleted during execution.")]
    public static long StorageDeleted => _mainStorageDeleted + _otherStorageDeleted;
    private static long _mainStorageDeleted;
    private static long _otherStorageDeleted;
    internal static long MainThreadStorageDeleted => _mainStorageDeleted;
    internal static void IncrementStorageDeleted() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainStorageDeleted : ref _otherStorageDeleted);

    [CounterMetric]
    [Description("Number of code writes during execution.")]
    public static long CodeWrites => _mainCodeWrites + _otherCodeWrites;
    private static long _mainCodeWrites;
    private static long _otherCodeWrites;
    internal static long MainThreadCodeWrites => _mainCodeWrites;
    internal static void IncrementCodeWrites() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainCodeWrites : ref _otherCodeWrites);

    [CounterMetric]
    [Description("Total bytes of code written during execution.")]
    public static long CodeBytesWritten => _mainCodeBytesWritten + _otherCodeBytesWritten;
    private static long _mainCodeBytesWritten;
    private static long _otherCodeBytesWritten;
    internal static long MainThreadCodeBytesWritten => _mainCodeBytesWritten;
    internal static void IncrementCodeBytesWritten(int bytes) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainCodeBytesWritten : ref _otherCodeBytesWritten, bytes);

    [CounterMetric]
    [Description("Number of EIP-7702 delegations set during execution.")]
    public static long Eip7702DelegationsSet => _mainEip7702DelegationsSet + _otherEip7702DelegationsSet;
    private static long _mainEip7702DelegationsSet;
    private static long _otherEip7702DelegationsSet;
    internal static long MainThreadEip7702DelegationsSet => _mainEip7702DelegationsSet;
    internal static void IncrementEip7702DelegationsSet() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainEip7702DelegationsSet : ref _otherEip7702DelegationsSet);

    [CounterMetric]
    [Description("Number of EIP-7702 delegations cleared during execution.")]
    public static long Eip7702DelegationsCleared => _mainEip7702DelegationsCleared + _otherEip7702DelegationsCleared;
    private static long _mainEip7702DelegationsCleared;
    private static long _otherEip7702DelegationsCleared;
    internal static long MainThreadEip7702DelegationsCleared => _mainEip7702DelegationsCleared;
    internal static void IncrementEip7702DelegationsCleared() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainEip7702DelegationsCleared : ref _otherEip7702DelegationsCleared);

    [Description("Time spent on state hashing/merkleization (ticks). Sum of storage merkle + state root.")]
    public static long StateHashTime => _mainStateHashTime + _otherStateHashTime;
    private static long _mainStateHashTime;
    private static long _otherStateHashTime;
    internal static long MainThreadStateHashTime => _mainStateHashTime;
    internal static void IncrementStateHashTime(long ticks) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStateHashTime : ref _otherStateHashTime, ticks);

    [Description("Time spent committing state to storage (ticks).")]
    public static long CommitTime => _mainCommitTime + _otherCommitTime;
    private static long _mainCommitTime;
    private static long _otherCommitTime;
    internal static long MainThreadCommitTime => _mainCommitTime;
    internal static void IncrementCommitTime(long ticks) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainCommitTime : ref _otherCommitTime, ticks);

    [Description("Time spent on storage trie merkleization — Commit(commitRoots: true) (ticks).")]
    public static long StorageMerkleTime => _mainStorageMerkleTime + _otherStorageMerkleTime;
    private static long _mainStorageMerkleTime;
    private static long _otherStorageMerkleTime;
    internal static long MainThreadStorageMerkleTime => _mainStorageMerkleTime;
    internal static void IncrementStorageMerkleTime(long ticks) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStorageMerkleTime : ref _otherStorageMerkleTime, ticks);

    [Description("Time spent on state root recalculation + commit tree (ticks).")]
    public static long StateRootTime => _mainStateRootTime + _otherStateRootTime;
    private static long _mainStateRootTime;
    private static long _otherStateRootTime;
    internal static long MainThreadStateRootTime => _mainStateRootTime;
    internal static void IncrementStateRootTime(long ticks) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStateRootTime : ref _otherStateRootTime, ticks);

    [Description("Time spent calculating bloom filters (ticks).")]
    public static long BloomsTime => _mainBloomsTime + _otherBloomsTime;
    private static long _mainBloomsTime;
    private static long _otherBloomsTime;
    internal static long MainThreadBloomsTime => _mainBloomsTime;
    internal static void IncrementBloomsTime(long ticks) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainBloomsTime : ref _otherBloomsTime, ticks);

    [Description("Time spent calculating receipts root (ticks).")]
    public static long ReceiptsRootTime => _mainReceiptsRootTime + _otherReceiptsRootTime;
    private static long _mainReceiptsRootTime;
    private static long _otherReceiptsRootTime;
    internal static long MainThreadReceiptsRootTime => _mainReceiptsRootTime;
    internal static void IncrementReceiptsRootTime(long ticks) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainReceiptsRootTime : ref _otherReceiptsRootTime, ticks);

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
