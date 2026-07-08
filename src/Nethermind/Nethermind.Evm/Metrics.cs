// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Threading;
using Nethermind.Int256;

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
/// builds), rebuild with the <c>NO_EXEC_METRICS</c> symbol defined: the JIT
/// folds <see cref="IsActive"/> to <c>false</c>, every <c>if (!ExecutionMetricsFlag.IsActive)
/// return;</c> guard becomes an unconditional early return, and with
/// <c>AggressiveInlining</c> the empty bodies are inlined into callers — eliminating both
/// the call and the atomic write.</para>
/// </remarks>
public readonly struct ExecutionMetricsFlag : IFlag
{
    public static bool IsActive =>
#if NO_EXEC_METRICS
        false;
#else
        true;
#endif
}

public class Metrics
{
    private static bool IsBlockProcessingThread => ProcessingThread.IsBlockProcessingThread;

    /// <summary>
    /// Per-thread cells for the hot opcode counters, summed on read.
    /// </summary>
    /// <remarks>
    /// These counters fire per executed opcode, concurrently from the main processing thread and
    /// every prewarmer thread. A shared <see cref="Interlocked"/> field makes each increment a
    /// contended cache-line acquisition (the main/other split shares a line, so speculative
    /// execution threads slow down the main loop via false sharing). Each thread instead gets its
    /// own cell — a plain single-writer add — and readers sum over all registered cells; cells of
    /// dead threads are kept so the counters stay monotonic.
    /// </remarks>
    private sealed class OpcodeCounters
    {
        public long MainCodeDbCache; public long OtherCodeDbCache;
        public long MainOpCodes; public long OtherOpCodes;
        public long MainSelfDestructs; public long OtherSelfDestructs;
        public long MainCalls; public long OtherCalls;
        public long MainSLoadOpcode; public long OtherSLoadOpcode;
        public long MainSStoreOpcode; public long OtherSStoreOpcode;
        public long MainEmptyCalls; public long OtherEmptyCalls;
        public long MainCreates; public long OtherCreates;
        public long MainContractsAnalysed; public long OtherContractsAnalysed;
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementCodeDbCache()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void IncrementOpCodes(int count)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainOpCodes += count; else counters.OtherOpCodes += count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddSLoadOpcode(long count) => AddSLoadOpcodes(count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddSLoadOpcodes(long count)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainSLoadOpcode += count; else counters.OtherSLoadOpcode += count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddSStoreOpcode(long count)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainSStoreOpcode += count; else counters.OtherSStoreOpcode += count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddStorageDeleted(long count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStorageDeleted.Value : ref _otherStorageDeleted.Value, count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddCalls(long count)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainCalls += count; else counters.OtherCalls += count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddEmptyCalls(long count)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainEmptyCalls += count; else counters.OtherEmptyCalls += count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddCreates(long count)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainCreates += count; else counters.OtherCreates += count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddSelfDestructs(long count)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainSelfDestructs += count; else counters.OtherSelfDestructs += count;
    }

    [CounterMetric]
    [Description("Number of SELFDESTRUCT calls.")]
    public static long SelfDestructs => SumOpcodeCounters(static c => c.MainSelfDestructs + c.OtherSelfDestructs);
    [Description("Number of SELFDESTRUCT calls on main processing thread.")]
    public static long MainThreadSelfDestructs => SumOpcodeCounters(static c => c.MainSelfDestructs);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void IncrementSelfDestructs() => AddSelfDestructs(1);

    [CounterMetric]
    [Description("Number of calls to other contracts.")]
    public static long Calls => SumOpcodeCounters(static c => c.MainCalls + c.OtherCalls);
    [Description("Number of calls to other contracts on main processing thread.")]
    public static long MainThreadCalls => SumOpcodeCounters(static c => c.MainCalls);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void IncrementCalls() => AddCalls(1);

    [CounterMetric]
    [Description("Number of SLOAD opcodes executed.")]
    public static long SloadOpcode => SumOpcodeCounters(static c => c.MainSLoadOpcode + c.OtherSLoadOpcode);
    [Description("Number of SLOAD opcodes executed on main processing thread.")]
    public static long MainThreadSLoadOpcode => SumOpcodeCounters(static c => c.MainSLoadOpcode);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void IncrementSLoadOpcode() => AddSLoadOpcodes(1);

    [CounterMetric]
    [Description("Number of SSTORE opcodes executed.")]
    public static long SstoreOpcode => SumOpcodeCounters(static c => c.MainSStoreOpcode + c.OtherSStoreOpcode);
    [Description("Number of SSTORE opcodes executed on main processing thread.")]
    public static long MainThreadSStoreOpcode => SumOpcodeCounters(static c => c.MainSStoreOpcode);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void IncrementSStoreOpcode() => AddSStoreOpcode(1);

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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void IncrementEmptyCalls() => AddEmptyCalls(1);

    [CounterMetric]
    [Description("Number of contract create calls.")]
    public static long Creates => SumOpcodeCounters(static c => c.MainCreates + c.OtherCreates);
    [Description("Number of contract create calls on main processing thread.")]
    public static long MainThreadCreates => SumOpcodeCounters(static c => c.MainCreates);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void IncrementCreates() => AddCreates(1);

    [Description("Number of contracts' code analysed for jump destinations.")]
    public static long ContractsAnalysed => SumOpcodeCounters(static c => c.MainContractsAnalysed + c.OtherContractsAnalysed);
    [Description("Number of contracts' code analysed for jump destinations on main processing thread.")]
    public static long MainThreadContractsAnalysed => SumOpcodeCounters(static c => c.MainContractsAnalysed);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void IncrementContractsAnalysed()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        OpcodeCounters counters = ThreadCounters;
        if (IsBlockProcessingThread) counters.MainContractsAnalysed++; else counters.OtherContractsAnalysed++;
    }

    // Cross-client execution metrics gated by ExecutionMetricsFlag.
    // Each Increment* method short-circuits when ExecutionMetricsFlag.IsActive is false:
    // since IsActive is a static property folded to a constant by the JIT, flipping the flag to
    // false elides the Interlocked.Increment / Interlocked.Add when inlined.

    private static CacheLinePaddedLong _mainCodeReads;
    internal static long MainThreadCodeReads => _mainCodeReads.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementCodeReads()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        if (!IsBlockProcessingThread) return;
        Interlocked.Increment(ref _mainCodeReads.Value);
    }

    private static CacheLinePaddedLong _mainCodeBytesRead;
    internal static long MainThreadCodeBytesRead => _mainCodeBytesRead.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementCodeBytesRead(int bytes)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        if (!IsBlockProcessingThread) return;
        Interlocked.Add(ref _mainCodeBytesRead.Value, bytes);
    }

    [CounterMetric]
    [Description("Number of account writes during execution.")]
    public static long AccountWrites => _mainAccountWrites.Value + _otherAccountWrites.Value;
    private static CacheLinePaddedLong _mainAccountWrites;
    private static CacheLinePaddedLong _otherAccountWrites;
    internal static long MainThreadAccountWrites => _mainAccountWrites.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddAccountWrites(long count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainAccountWrites.Value : ref _otherAccountWrites.Value, count);

    [CounterMetric]
    [Description("Number of accounts deleted during execution.")]
    public static long AccountDeleted => _mainAccountDeleted.Value + _otherAccountDeleted.Value;
    private static CacheLinePaddedLong _mainAccountDeleted;
    private static CacheLinePaddedLong _otherAccountDeleted;
    internal static long MainThreadAccountDeleted => _mainAccountDeleted.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddAccountDeleted(long count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainAccountDeleted.Value : ref _otherAccountDeleted.Value, count);

    [CounterMetric]
    [Description("Number of storage slot writes during execution.")]
    public static long StorageWrites => _mainStorageWrites.Value + _otherStorageWrites.Value;
    private static CacheLinePaddedLong _mainStorageWrites;
    private static CacheLinePaddedLong _otherStorageWrites;
    internal static long MainThreadStorageWrites => _mainStorageWrites.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddStorageWrites(long count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStorageWrites.Value : ref _otherStorageWrites.Value, count);

    [CounterMetric]
    [Description("Number of storage slots deleted during execution.")]
    public static long StorageDeleted => _mainStorageDeleted.Value + _otherStorageDeleted.Value;
    private static CacheLinePaddedLong _mainStorageDeleted;
    private static CacheLinePaddedLong _otherStorageDeleted;
    internal static long MainThreadStorageDeleted => _mainStorageDeleted.Value;

    [CounterMetric]
    [Description("Number of code writes during execution.")]
    public static long CodeWrites => _mainCodeWrites.Value + _otherCodeWrites.Value;
    private static CacheLinePaddedLong _mainCodeWrites;
    private static CacheLinePaddedLong _otherCodeWrites;
    internal static long MainThreadCodeWrites => _mainCodeWrites.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddCodeWrites(long count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainCodeWrites.Value : ref _otherCodeWrites.Value, count);

    [CounterMetric]
    [Description("Total bytes of code written during execution.")]
    public static long CodeBytesWritten => _mainCodeBytesWritten.Value + _otherCodeBytesWritten.Value;
    private static CacheLinePaddedLong _mainCodeBytesWritten;
    private static CacheLinePaddedLong _otherCodeBytesWritten;
    internal static long MainThreadCodeBytesWritten => _mainCodeBytesWritten.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddCodeBytesWritten(long count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainCodeBytesWritten.Value : ref _otherCodeBytesWritten.Value, count);

    [CounterMetric]
    [Description("Number of EIP-7702 delegations set during execution.")]
    public static long Eip7702DelegationsSet => _mainEip7702DelegationsSet.Value + _otherEip7702DelegationsSet.Value;
    private static CacheLinePaddedLong _mainEip7702DelegationsSet;
    private static CacheLinePaddedLong _otherEip7702DelegationsSet;
    internal static long MainThreadEip7702DelegationsSet => _mainEip7702DelegationsSet.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementEip7702DelegationsSet()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainEip7702DelegationsSet.Value : ref _otherEip7702DelegationsSet.Value);
    }

    [CounterMetric]
    [Description("Number of EIP-7702 delegations cleared during execution.")]
    public static long Eip7702DelegationsCleared => _mainEip7702DelegationsCleared.Value + _otherEip7702DelegationsCleared.Value;
    private static CacheLinePaddedLong _mainEip7702DelegationsCleared;
    private static CacheLinePaddedLong _otherEip7702DelegationsCleared;
    internal static long MainThreadEip7702DelegationsCleared => _mainEip7702DelegationsCleared.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementEip7702DelegationsCleared()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainEip7702DelegationsCleared.Value : ref _otherEip7702DelegationsCleared.Value);
    }

    // Timing counters below accumulate elapsed <see cref="TimeSpan"/> ticks (100 ns), as produced
    // by <see cref="Stopwatch.GetElapsedTime"/>.<see cref="TimeSpan.Ticks"/> — NOT raw
    // <see cref="Stopwatch"/> timestamp ticks. Consumers convert to ms by dividing by
    // <see cref="TimeSpan.TicksPerMillisecond"/>.

    [Description("Time spent on state hashing/merkleization (TimeSpan ticks). Sum of storage merkle + state root.")]
    public static long StateHashTime => _mainStateHashTime.Value + _otherStateHashTime.Value;
    private static CacheLinePaddedLong _mainStateHashTime;
    private static CacheLinePaddedLong _otherStateHashTime;
    internal static long MainThreadStateHashTime => _mainStateHashTime.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementStateHashTime(long ticks)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStateHashTime.Value : ref _otherStateHashTime.Value, ticks);
    }

    [Description("Time spent committing state to storage (ticks).")]
    public static long CommitTime => _mainCommitTime.Value + _otherCommitTime.Value;
    private static CacheLinePaddedLong _mainCommitTime;
    private static CacheLinePaddedLong _otherCommitTime;
    internal static long MainThreadCommitTime => _mainCommitTime.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementCommitTime(long ticks)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Add(ref IsBlockProcessingThread ? ref _mainCommitTime.Value : ref _otherCommitTime.Value, ticks);
    }

    [Description("Time spent on storage trie merkleization — Commit(commitRoots: true) (ticks).")]
    public static long StorageMerkleTime => _mainStorageMerkleTime.Value + _otherStorageMerkleTime.Value;
    private static CacheLinePaddedLong _mainStorageMerkleTime;
    private static CacheLinePaddedLong _otherStorageMerkleTime;
    internal static long MainThreadStorageMerkleTime => _mainStorageMerkleTime.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementStorageMerkleTime(long ticks)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStorageMerkleTime.Value : ref _otherStorageMerkleTime.Value, ticks);
    }

    [Description("Time spent on state root recalculation + commit tree (ticks).")]
    public static long StateRootTime => _mainStateRootTime.Value + _otherStateRootTime.Value;
    private static CacheLinePaddedLong _mainStateRootTime;
    private static CacheLinePaddedLong _otherStateRootTime;
    internal static long MainThreadStateRootTime => _mainStateRootTime.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementStateRootTime(long ticks)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Add(ref IsBlockProcessingThread ? ref _mainStateRootTime.Value : ref _otherStateRootTime.Value, ticks);
    }

    [Description("Time spent calculating bloom filters (ticks).")]
    public static long BloomsTime => _mainBloomsTime.Value + _otherBloomsTime.Value;
    private static CacheLinePaddedLong _mainBloomsTime;
    private static CacheLinePaddedLong _otherBloomsTime;
    internal static long MainThreadBloomsTime => _mainBloomsTime.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementBloomsTime(long ticks)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Add(ref IsBlockProcessingThread ? ref _mainBloomsTime.Value : ref _otherBloomsTime.Value, ticks);
    }

    [Description("Time spent calculating receipts root (ticks).")]
    public static long ReceiptsRootTime => _mainReceiptsRootTime.Value + _otherReceiptsRootTime.Value;
    private static CacheLinePaddedLong _mainReceiptsRootTime;
    private static CacheLinePaddedLong _otherReceiptsRootTime;
    internal static long MainThreadReceiptsRootTime => _mainReceiptsRootTime.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementReceiptsRootTime(long ticks)
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Add(ref IsBlockProcessingThread ? ref _mainReceiptsRootTime.Value : ref _otherReceiptsRootTime.Value, ticks);
    }

    [GaugeMetric]
    [Description("The number of tasks currently scheduled in the background.")]
    public static long NumberOfBackgroundTasksScheduled { get; set; }

    private static CacheLinePaddedLong _totalBackgroundTasksQueued;
    [GaugeMetric]
    [Description("Total number of tasks queued for background execution.")]
    public static long TotalBackgroundTasksQueued => _totalBackgroundTasksQueued.Value;
    public static void IncrementTotalBackgroundTasksQueued() => Interlocked.Increment(ref _totalBackgroundTasksQueued.Value);

    private static CacheLinePaddedLong _totalBackgroundTasksDropped;
    [GaugeMetric]
    [Description("Total number of background tasks dropped because queue was full.")]
    public static long TotalBackgroundTasksDropped => _totalBackgroundTasksDropped.Value;
    public static void IncrementTotalBackgroundTasksDropped() => Interlocked.Increment(ref _totalBackgroundTasksDropped.Value);

    private static CacheLinePaddedLong _totalBackgroundTasksExecuted;
    [GaugeMetric]
    [Description("Total number of background tasks executed.")]
    public static long TotalBackgroundTasksExecuted => _totalBackgroundTasksExecuted.Value;
    public static void IncrementTotalBackgroundTasksExecuted() => Interlocked.Increment(ref _totalBackgroundTasksExecuted.Value);

    // Lock-free per-tx block gas-price aggregates: each packs two interdependent values into one long
    // CAS'd atomically - (min, max) and (count, running average). Not cache-line padded: they are
    // true-shared (every worker CASes all three per tx), so padding cannot reduce the inherent contention.
    private static long _minMaxGasPriceBits = PackFloats(float.MaxValue, 0f);
    private static long _countAveGasPriceBits;
    // Order-dependent streaming estimate (already non-deterministic under parallelism); its own CAS.
    private static float _blockEstMedianGasPrice;

    internal static long BlockTransactions => LoInt(Volatile.Read(ref _countAveGasPriceBits));
    internal static float BlockAveGasPrice => HiFloat(Volatile.Read(ref _countAveGasPriceBits));
    internal static float BlockMinGasPrice => LoFloat(Volatile.Read(ref _minMaxGasPriceBits));
    internal static float BlockMaxGasPrice => HiFloat(Volatile.Read(ref _minMaxGasPriceBits));
    internal static float BlockEstMedianGasPrice => Volatile.Read(ref _blockEstMedianGasPrice);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackFloats(float lo, float hi)
        => (uint)BitConverter.SingleToInt32Bits(lo) | ((long)(uint)BitConverter.SingleToInt32Bits(hi) << 32);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackCountAve(int count, float ave)
        => (uint)count | ((long)(uint)BitConverter.SingleToInt32Bits(ave) << 32);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LoFloat(long bits) => BitConverter.Int32BitsToSingle((int)bits);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HiFloat(long bits) => BitConverter.Int32BitsToSingle((int)(bits >> 32));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LoInt(long bits) => (int)bits;

    /// <summary>
    /// Gets block gas price data for external access. Returns (min, estMedian, ave, max).
    /// Returns null if no gas data available (min is float.MaxValue).
    /// </summary>
    public static (float Min, float EstMedian, float Ave, float Max)? GetBlockGasPrices()
    {
        float min = BlockMinGasPrice;
        return min == float.MaxValue ? null : (min, BlockEstMedianGasPrice, BlockAveGasPrice, BlockMaxGasPrice);
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
        Volatile.Write(ref _minMaxGasPriceBits, PackFloats(float.MaxValue, 0f));
        Volatile.Write(ref _countAveGasPriceBits, 0L);
        Volatile.Write(ref _blockEstMedianGasPrice, 0f);
    }

    /// <summary>Folds a transaction's effective gas price into the per-block aggregates (lock-free).</summary>
    /// <remarks>
    /// Prices >= <see cref="ulong.MaxValue"/> wei/gas (~18.4 ETH) are not meaningful and are skipped,
    /// avoiding the multi-limb <see cref="UInt256"/>-to-<see cref="double"/> conversion.
    /// </remarks>
    internal static void UpdateBlockGasPrice(in UInt256 effectiveGasPrice)
    {
        if (!effectiveGasPrice.IsUint64) return;

        float gasPrice = (float)(effectiveGasPrice.u0 / 1_000_000_000.0);

        long mm = Volatile.Read(ref _minMaxGasPriceBits);
        while (true)
        {
            long updated = PackFloats(MathF.Min(LoFloat(mm), gasPrice), MathF.Max(HiFloat(mm), gasPrice));
            if (updated == mm) break;
            long prev = Interlocked.CompareExchange(ref _minMaxGasPriceBits, updated, mm);
            if (prev == mm) break;
            mm = prev;
        }

        float newAve;
        long ca = Volatile.Read(ref _countAveGasPriceBits);
        while (true)
        {
            int count = LoInt(ca);
            newAve = (HiFloat(ca) * count + gasPrice) / (count + 1);
            long prev = Interlocked.CompareExchange(ref _countAveGasPriceBits, PackCountAve(count + 1, newAve), ca);
            if (prev == ca) break;
            ca = prev;
        }

        float median = Volatile.Read(ref _blockEstMedianGasPrice);
        while (true)
        {
            float updated = median + newAve * 0.01f * float.Sign(gasPrice - median);
            if (updated == median) break;
            float prev = Interlocked.CompareExchange(ref _blockEstMedianGasPrice, updated, median);
            if (prev == median) break;
            median = prev;
        }
        // Gauges published once by PublishBlockGasPriceGauges after workers join, not here (a slow
        // worker could otherwise leave a stale value).
    }

    /// <summary>
    /// Seeds the gas-price aggregates with the block base fee when no transaction contributed (empty /
    /// system-only block). Skips zero base fee (pre-EIP-1559, genesis) - "0.000" is less useful than blank.
    /// </summary>
    internal static void SeedBlockGasPriceIfEmpty(in UInt256 baseFee)
    {
        if (!baseFee.IsUint64 || baseFee.IsZero) return;

        float gasPrice = (float)(baseFee.u0 / 1_000_000_000.0);

        long empty = PackFloats(float.MaxValue, 0f);
        if (Interlocked.CompareExchange(ref _minMaxGasPriceBits, PackFloats(gasPrice, gasPrice), empty) != empty)
            return; // a transaction already contributed

        // Only ever called after all tx workers have joined, so no concurrent UpdateBlockGasPrice can
        // observe the gap between the CAS above and these non-atomic seed writes.
        Volatile.Write(ref _countAveGasPriceBits, PackCountAve(0, gasPrice));
        Volatile.Write(ref _blockEstMedianGasPrice, gasPrice);
    }

    /// <summary>
    /// Publishes the latest-block gas-price gauges from the final aggregates. Call once after all
    /// (possibly parallel) transactions are processed, so a slow worker cannot leave a stale value.
    /// </summary>
    internal static void PublishBlockGasPriceGauges()
    {
        float min = BlockMinGasPrice;
        if (min == float.MaxValue) return; // no data this block; keep previous gauge values

        GasPriceMin = min;
        GasPriceMax = BlockMaxGasPrice;
        GasPriceAve = BlockAveGasPrice;
        GasPriceMedian = BlockEstMedianGasPrice;
    }
}
