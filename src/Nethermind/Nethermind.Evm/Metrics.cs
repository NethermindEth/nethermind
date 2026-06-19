// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Threading;

[assembly: InternalsVisibleTo("Nethermind.Consensus")]
[assembly: InternalsVisibleTo("Nethermind.State")]
[assembly: InternalsVisibleTo("Nethermind.Core.Test")]
[assembly: InternalsVisibleTo("Nethermind.Consensus.Test")]
[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

namespace Nethermind.Evm;

/// <summary>
/// Compile-time switch for the cross-client execution metrics
/// (writes, code-read diagnostics, EIP-7702 delegation set/clear, block timing breakdown).
/// </summary>
/// <remarks>
/// <para>This is a compile-time gate, deliberately not tied to runtime configuration. Default
/// builds run with metrics on — every gated <see cref="Metrics"/> Increment* call fires its
/// underlying <see cref="Interlocked"/> op (~10–15 ns on x64), <b>regardless of</b>
/// <see cref="BlocksConfig.SlowBlockThresholdMs"/>. The threshold gates slow-block JSON
/// emission only, not the exported counter increments.</para>
/// <para>To eliminate the counter overhead entirely (e.g. for performance-critical benchmark
/// builds), rebuild with the <c>NETHERMIND_NO_EXECUTION_METRICS</c> symbol defined: the JIT
/// folds <see cref="IsActive"/> to <c>false</c>, every <c>if (!ExecutionMetricsFlag.IsActive)
/// return;</c> guard becomes an unconditional early return, and with
/// <c>AggressiveInlining</c> the empty bodies are inlined into callers — eliminating both
/// the call and the atomic write.</para>
/// <para>Pre-existing counters (Calls, SLoad/SStore, CodeDbCache, …) are not gated by this
/// flag; their cost is unaffected by the symbol.</para>
/// </remarks>
public readonly struct ExecutionMetricsFlag : IFlag
{
    public static bool IsActive =>
#if NETHERMIND_NO_EXECUTION_METRICS
        false;
#else
        true;
#endif
}

public class Metrics
{
    private static bool IsBlockProcessingThread => ProcessingThread.IsBlockProcessingThread;

    private sealed class OpcodeCounters
    {
        public long MainCodeDbCache;
        public long OtherCodeDbCache;
        public long MainOpCodes;
        public long OtherOpCodes;
        public long MainSelfDestructs;
        public long OtherSelfDestructs;
        public long MainCalls;
        public long OtherCalls;
        public long MainSLoadOpcode;
        public long OtherSLoadOpcode;
        public long MainSStoreOpcode;
        public long OtherSStoreOpcode;
        public long MainEmptyCalls;
        public long OtherEmptyCalls;
        public long MainCreates;
        public long OtherCreates;
        public long MainContractsAnalysed;
        public long OtherContractsAnalysed;
        public OpcodeCounters? Next;
    }

    private static OpcodeCounters? s_allOpcodeCounters;

    [ThreadStatic]
    private static OpcodeCounters? t_opcodeCounters;

    private static OpcodeCounters ThreadCounters
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => t_opcodeCounters ?? CreateThreadCounters();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static OpcodeCounters CreateThreadCounters()
    {
        OpcodeCounters counters = new();
        OpcodeCounters? head;
        do
        {
            head = s_allOpcodeCounters;
            counters.Next = head;
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref s_allOpcodeCounters, counters, head), head));

        return t_opcodeCounters = counters;
    }

    private static long SumOpcodeCounters(Func<OpcodeCounters, long> selector)
    {
        long sum = 0;
        for (OpcodeCounters? counters = s_allOpcodeCounters; counters is not null; counters = counters.Next)
        {
            sum += selector(counters);
        }

        return sum;
    }

    [CounterMetric]
    [Description("Number of Code DB cache reads.")]
    public static long CodeDbCache => SumOpcodeCounters(static c => c.MainCodeDbCache + c.OtherCodeDbCache);
    [Description("Number of Code DB cache reads on main processing thread.")]
    public static long MainThreadCodeDbCache => SumOpcodeCounters(static c => c.MainCodeDbCache);
    internal static void IncrementCodeDbCache()
    {
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainCodeDbCache++; else counters.OtherCodeDbCache++;
    }

    [CounterMetric]
    [Description("Number of EVM exceptions thrown by contracts.")]
    public static long EvmExceptions { get; set; }

    [CounterMetric]
    [Description("Number of opcodes executed.")]
    public static long OpCodes => SumOpcodeCounters(static c => c.MainOpCodes + c.OtherOpCodes);
    [Description("Number of opcodes executed on main processing thread.")]
    public static long MainThreadOpCodes => SumOpcodeCounters(static c => c.MainOpCodes);
    public static void IncrementOpCodes(int count)
    {
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainOpCodes += count; else counters.OtherOpCodes += count;
    }

    [CounterMetric]
    [Description("Number of SELFDESTRUCT calls.")]
    public static long SelfDestructs => SumOpcodeCounters(static c => c.MainSelfDestructs + c.OtherSelfDestructs);
    [Description("Number of SELFDESTRUCT calls on main processing thread.")]
    public static long MainThreadSelfDestructs => SumOpcodeCounters(static c => c.MainSelfDestructs);
    public static void IncrementSelfDestructs()
    {
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainSelfDestructs++; else counters.OtherSelfDestructs++;
    }

    [CounterMetric]
    [Description("Number of calls to other contracts.")]
    public static long Calls => SumOpcodeCounters(static c => c.MainCalls + c.OtherCalls);
    [Description("Number of calls to other contracts on main processing thread.")]
    public static long MainThreadCalls => SumOpcodeCounters(static c => c.MainCalls);
    public static void IncrementCalls()
    {
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainCalls++; else counters.OtherCalls++;
    }

    [CounterMetric]
    [Description("Number of SLOAD opcodes executed.")]
    public static long SloadOpcode => SumOpcodeCounters(static c => c.MainSLoadOpcode + c.OtherSLoadOpcode);
    [Description("Number of SLOAD opcodes executed on main processing thread.")]
    public static long MainThreadSLoadOpcode => SumOpcodeCounters(static c => c.MainSLoadOpcode);
    public static void IncrementSLoadOpcode()
    {
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainSLoadOpcode++; else counters.OtherSLoadOpcode++;
    }

    public static void AddSLoadOpcodes(int count)
    {
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainSLoadOpcode += count; else counters.OtherSLoadOpcode += count;
    }

    [CounterMetric]
    [Description("Number of SSTORE opcodes executed.")]
    public static long SstoreOpcode => SumOpcodeCounters(static c => c.MainSStoreOpcode + c.OtherSStoreOpcode);
    [Description("Number of SSTORE opcodes executed on main processing thread.")]
    public static long MainThreadSStoreOpcode => SumOpcodeCounters(static c => c.MainSStoreOpcode);
    public static void IncrementSStoreOpcode()
    {
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainSStoreOpcode++; else counters.OtherSStoreOpcode++;
    }

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
    public static long EmptyCalls => SumOpcodeCounters(static c => c.MainEmptyCalls + c.OtherEmptyCalls);
    [Description("Number of calls made to addresses without code on main processing thread.")]
    public static long MainThreadEmptyCalls => SumOpcodeCounters(static c => c.MainEmptyCalls);
    public static void IncrementEmptyCalls()
    {
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainEmptyCalls++; else counters.OtherEmptyCalls++;
    }

    [CounterMetric]
    [Description("Number of contract create calls.")]
    public static long Creates => SumOpcodeCounters(static c => c.MainCreates + c.OtherCreates);
    [Description("Number of contract create calls on main processing thread.")]
    public static long MainThreadCreates => SumOpcodeCounters(static c => c.MainCreates);
    public static void IncrementCreates()
    {
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainCreates++; else counters.OtherCreates++;
    }

    [Description("Number of contracts' code analysed for jump destinations.")]
    public static long ContractsAnalysed => SumOpcodeCounters(static c => c.MainContractsAnalysed + c.OtherContractsAnalysed);
    [Description("Number of contracts' code analysed for jump destinations on main processing thread.")]
    public static long MainThreadContractsAnalysed => SumOpcodeCounters(static c => c.MainContractsAnalysed);
    public static void IncrementContractsAnalysed()
    {
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainContractsAnalysed++; else counters.OtherContractsAnalysed++;
    }

    // Cross-client execution metrics gated by ExecutionMetricsFlag.
    // Each Increment* method short-circuits when ExecutionMetricsFlag.IsActive is false:
    // since IsActive is a static property folded to a constant by the JIT, flipping the flag to
    // false elides the Interlocked.Increment / Interlocked.Add when inlined.

    private static long _mainCodeReads;
    internal static long MainThreadCodeReads => _mainCodeReads;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementCodeReads()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        if (!IsBlockProcessingThread) return;
        Interlocked.Increment(ref _mainCodeReads);
    }

    private static long _mainCodeBytesRead;
    internal static long MainThreadCodeBytesRead => _mainCodeBytesRead;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementCodeBytesRead(int bytes)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        if (!IsBlockProcessingThread) return;
        Interlocked.Add(ref _mainCodeBytesRead, bytes);
    }

    [CounterMetric]
    [Description("Number of account writes during execution.")]
    public static long AccountWrites => _mainAccountWrites + _otherAccountWrites;
    private static long _mainAccountWrites;
    private static long _otherAccountWrites;
    internal static long MainThreadAccountWrites => _mainAccountWrites;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementAccountWrites()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainAccountWrites : ref _otherAccountWrites);
    }

    [CounterMetric]
    [Description("Number of accounts deleted during execution.")]
    public static long AccountDeleted => _mainAccountDeleted + _otherAccountDeleted;
    private static long _mainAccountDeleted;
    private static long _otherAccountDeleted;
    internal static long MainThreadAccountDeleted => _mainAccountDeleted;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementAccountDeleted()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainAccountDeleted : ref _otherAccountDeleted);
    }

    [CounterMetric]
    [Description("Number of storage slot writes during execution.")]
    public static long StorageWrites => _mainStorageWrites + _otherStorageWrites;
    private static long _mainStorageWrites;
    private static long _otherStorageWrites;
    internal static long MainThreadStorageWrites => _mainStorageWrites;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementStorageWrites()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainStorageWrites : ref _otherStorageWrites);
    }

    [CounterMetric]
    [Description("Number of storage slots deleted during execution.")]
    public static long StorageDeleted => _mainStorageDeleted + _otherStorageDeleted;
    private static long _mainStorageDeleted;
    private static long _otherStorageDeleted;
    internal static long MainThreadStorageDeleted => _mainStorageDeleted;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementStorageDeleted()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainStorageDeleted : ref _otherStorageDeleted);
    }

    [CounterMetric]
    [Description("Number of code writes during execution.")]
    public static long CodeWrites => _mainCodeWrites + _otherCodeWrites;
    private static long _mainCodeWrites;
    private static long _otherCodeWrites;
    internal static long MainThreadCodeWrites => _mainCodeWrites;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementCodeWrites()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainCodeWrites : ref _otherCodeWrites);
    }

    [CounterMetric]
    [Description("Total bytes of code written during execution.")]
    public static long CodeBytesWritten => _mainCodeBytesWritten + _otherCodeBytesWritten;
    private static long _mainCodeBytesWritten;
    private static long _otherCodeBytesWritten;
    internal static long MainThreadCodeBytesWritten => _mainCodeBytesWritten;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementCodeBytesWritten(int bytes)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Add(ref IsBlockProcessingThread ? ref _mainCodeBytesWritten : ref _otherCodeBytesWritten, bytes);
    }

    [CounterMetric]
    [Description("Number of EIP-7702 delegations set during execution.")]
    public static long Eip7702DelegationsSet => _mainEip7702DelegationsSet + _otherEip7702DelegationsSet;
    private static long _mainEip7702DelegationsSet;
    private static long _otherEip7702DelegationsSet;
    internal static long MainThreadEip7702DelegationsSet => _mainEip7702DelegationsSet;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementEip7702DelegationsSet()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainEip7702DelegationsSet : ref _otherEip7702DelegationsSet);
    }

    [CounterMetric]
    [Description("Number of EIP-7702 delegations cleared during execution.")]
    public static long Eip7702DelegationsCleared => _mainEip7702DelegationsCleared + _otherEip7702DelegationsCleared;
    private static long _mainEip7702DelegationsCleared;
    private static long _otherEip7702DelegationsCleared;
    internal static long MainThreadEip7702DelegationsCleared => _mainEip7702DelegationsCleared;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementEip7702DelegationsCleared()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainEip7702DelegationsCleared : ref _otherEip7702DelegationsCleared);
    }

    // Timing counters below accumulate elapsed <see cref="TimeSpan"/> ticks (100 ns), as produced
    // by <see cref="Stopwatch.GetElapsedTime"/>.<see cref="TimeSpan.Ticks"/> — NOT raw
    // <see cref="Stopwatch"/> timestamp ticks. Consumers convert to ms by dividing by
    // <see cref="TimeSpan.TicksPerMillisecond"/>.

    [Description("Time spent on state hashing/merkleization (TimeSpan ticks). Sum of storage merkle + state root.")]
    public static long StateHashTime => _mainStateHashTime + _otherStateHashTime;
    private static long _mainStateHashTime;
    private static long _otherStateHashTime;
    internal static long MainThreadStateHashTime => _mainStateHashTime;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementStateHashTime(long ticks)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStateHashTime : ref _otherStateHashTime, ticks);
    }

    [Description("Time spent committing state to storage (ticks).")]
    public static long CommitTime => _mainCommitTime + _otherCommitTime;
    private static long _mainCommitTime;
    private static long _otherCommitTime;
    internal static long MainThreadCommitTime => _mainCommitTime;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementCommitTime(long ticks)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Add(ref IsBlockProcessingThread ? ref _mainCommitTime : ref _otherCommitTime, ticks);
    }

    [Description("Time spent on storage trie merkleization — Commit(commitRoots: true) (ticks).")]
    public static long StorageMerkleTime => _mainStorageMerkleTime + _otherStorageMerkleTime;
    private static long _mainStorageMerkleTime;
    private static long _otherStorageMerkleTime;
    internal static long MainThreadStorageMerkleTime => _mainStorageMerkleTime;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementStorageMerkleTime(long ticks)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStorageMerkleTime : ref _otherStorageMerkleTime, ticks);
    }

    [Description("Time spent on state root recalculation + commit tree (ticks).")]
    public static long StateRootTime => _mainStateRootTime + _otherStateRootTime;
    private static long _mainStateRootTime;
    private static long _otherStateRootTime;
    internal static long MainThreadStateRootTime => _mainStateRootTime;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementStateRootTime(long ticks)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStateRootTime : ref _otherStateRootTime, ticks);
    }

    [Description("Time spent calculating bloom filters (ticks).")]
    public static long BloomsTime => _mainBloomsTime + _otherBloomsTime;
    private static long _mainBloomsTime;
    private static long _otherBloomsTime;
    internal static long MainThreadBloomsTime => _mainBloomsTime;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementBloomsTime(long ticks)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Add(ref IsBlockProcessingThread ? ref _mainBloomsTime : ref _otherBloomsTime, ticks);
    }

    [Description("Time spent calculating receipts root (ticks).")]
    public static long ReceiptsRootTime => _mainReceiptsRootTime + _otherReceiptsRootTime;
    private static long _mainReceiptsRootTime;
    private static long _otherReceiptsRootTime;
    internal static long MainThreadReceiptsRootTime => _mainReceiptsRootTime;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementReceiptsRootTime(long ticks)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Add(ref IsBlockProcessingThread ? ref _mainReceiptsRootTime : ref _otherReceiptsRootTime, ticks);
    }

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
