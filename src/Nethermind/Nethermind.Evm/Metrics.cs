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

    [CounterMetric]
    [Description("Number of Code DB cache reads.")]
    public static long CodeDbCache => _mainCodeDbCache.Value + _otherCodeDbCache.Value;
    private static CacheLinePaddedLong _mainCodeDbCache;
    private static CacheLinePaddedLong _otherCodeDbCache;
    [Description("Number of Code DB cache reads on main processing thread.")]
    public static long MainThreadCodeDbCache => _mainCodeDbCache.Value;
    internal static void IncrementCodeDbCache() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainCodeDbCache.Value : ref _otherCodeDbCache.Value);
    [CounterMetric]
    [Description("Number of EVM exceptions thrown by contracts.")]
    public static long EvmExceptions { get; set; }

    [CounterMetric]
    [Description("Number of opcodes executed.")]
    public static long OpCodes => _mainOpCodes.Value + _otherOpCodes.Value;
    private static CacheLinePaddedLong _mainOpCodes;
    private static CacheLinePaddedLong _otherOpCodes;
    [Description("Number of opcodes executed on main processing thread.")]
    public static long MainThreadOpCodes => _mainOpCodes.Value;
    public static void IncrementOpCodes(int count) => Interlocked.Add(ref IsBlockProcessingThread ? ref _mainOpCodes.Value : ref _otherOpCodes.Value, count);

    [CounterMetric]
    [Description("Number of SELFDESTRUCT calls.")]
    public static long SelfDestructs => _mainSelfDestructs.Value + _otherSelfDestructs.Value;
    private static CacheLinePaddedLong _mainSelfDestructs;
    private static CacheLinePaddedLong _otherSelfDestructs;
    [Description("Number of SELFDESTRUCT calls on main processing thread.")]
    public static long MainThreadSelfDestructs => _mainSelfDestructs.Value;
    public static void IncrementSelfDestructs() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainSelfDestructs.Value : ref _otherSelfDestructs.Value);

    [CounterMetric]
    [Description("Number of calls to other contracts.")]
    public static long Calls => _mainCalls.Value + _otherCalls.Value;
    private static CacheLinePaddedLong _mainCalls;
    private static CacheLinePaddedLong _otherCalls;
    [Description("Number of calls to other contracts on main processing thread.")]
    public static long MainThreadCalls => _mainCalls.Value;
    public static void IncrementCalls() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainCalls.Value : ref _otherCalls.Value);

    [CounterMetric]
    [Description("Number of SLOAD opcodes executed.")]
    public static long SloadOpcode => _mainSLoadOpcode.Value + _otherSLoadOpcode.Value;
    private static CacheLinePaddedLong _mainSLoadOpcode;
    private static CacheLinePaddedLong _otherSLoadOpcode;
    [Description("Number of SLOAD opcodes executed on main processing thread.")]
    public static long MainThreadSLoadOpcode => _mainSLoadOpcode.Value;
    public static void IncrementSLoadOpcode() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainSLoadOpcode.Value : ref _otherSLoadOpcode.Value);

    [CounterMetric]
    [Description("Number of SSTORE opcodes executed.")]
    public static long SstoreOpcode => _mainSStoreOpcode.Value + _otherSStoreOpcode.Value;
    private static CacheLinePaddedLong _mainSStoreOpcode;
    private static CacheLinePaddedLong _otherSStoreOpcode;
    [Description("Number of SSTORE opcodes executed on main processing thread.")]
    public static long MainThreadSStoreOpcode => _mainSStoreOpcode.Value;
    public static void IncrementSStoreOpcode() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainSStoreOpcode.Value : ref _otherSStoreOpcode.Value);

    [CounterMetric]
    [Description("Number of calls made to addresses without code.")]
    public static long EmptyCalls => _mainEmptyCalls.Value + _otherEmptyCalls.Value;
    private static CacheLinePaddedLong _mainEmptyCalls;
    private static CacheLinePaddedLong _otherEmptyCalls;
    [Description("Number of calls made to addresses without code on main processing thread.")]
    public static long MainThreadEmptyCalls => _mainEmptyCalls.Value;
    public static void IncrementEmptyCalls() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainEmptyCalls.Value : ref _otherEmptyCalls.Value);

    [CounterMetric]
    [Description("Number of contract create calls.")]
    public static long Creates => _mainCreates.Value + _otherCreates.Value;
    private static CacheLinePaddedLong _mainCreates;
    private static CacheLinePaddedLong _otherCreates;
    [Description("Number of contract create calls on main processing thread.")]
    public static long MainThreadCreates => _mainCreates.Value;
    public static void IncrementCreates() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainCreates.Value : ref _otherCreates.Value);

    [Description("Number of contracts' code analysed for jump destinations.")]
    public static long ContractsAnalysed => _mainContractsAnalysed.Value + _otherContractsAnalysed.Value;
    private static CacheLinePaddedLong _mainContractsAnalysed;
    private static CacheLinePaddedLong _otherContractsAnalysed;
    [Description("Number of contracts' code analysed for jump destinations on main processing thread.")]
    public static long MainThreadContractsAnalysed => _mainContractsAnalysed.Value;
    public static void IncrementContractsAnalysed() => Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainContractsAnalysed.Value : ref _otherContractsAnalysed.Value);

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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void IncrementStorageDeleted()
    {
        if (!ExecutionMetricsFlag.IsActive) return;
        Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainStorageDeleted.Value : ref _otherStorageDeleted.Value);
    }

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

    // Block gas-price aggregates are updated once per transaction and, under parallel BAL validation, by
    // many workers concurrently. They are kept lock-free by packing two interdependent values into one
    // long updated with a single CAS: _minMaxGasPriceBits holds (min, max); _countAveGasPriceBits holds
    // (count, running average) - packing count+ave together keeps the running mean exact under contention.
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

    /// <summary>Folds a transaction's effective gas price into the per-block aggregates.</summary>
    /// <remarks>
    /// Lock-free: parallel BAL workers each call this once per transaction. Gas prices at or above
    /// <see cref="ulong.MaxValue"/> wei/gas (~18.4 ETH) are not meaningful for these metrics, so the rare
    /// wider value is skipped rather than paying the multi-limb <see cref="UInt256"/> to
    /// <see cref="double"/> conversion.
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
        // Gauges are not published here: a slow parallel worker could overwrite them with a stale view.
        // PublishBlockGasPriceGauges() publishes once from the final aggregates after workers join.
    }

    /// <summary>
    /// Seeds the block gas-price aggregates with the block base fee when no user transaction
    /// contributed (empty or system-only block), so the report shows a meaningful value.
    /// </summary>
    /// <remarks>
    /// Skips zero-base-fee chains (pre-EIP-1559, some rollups, genesis): rendering "0.000" there is
    /// less informative than the prior blank output. Called after workers join, so the CAS is uncontended.
    /// </remarks>
    internal static void SeedBlockGasPriceIfEmpty(in UInt256 baseFee)
    {
        if (!baseFee.IsUint64 || baseFee.IsZero) return;

        float gasPrice = (float)(baseFee.u0 / 1_000_000_000.0);

        long empty = PackFloats(float.MaxValue, 0f);
        if (Interlocked.CompareExchange(ref _minMaxGasPriceBits, PackFloats(gasPrice, gasPrice), empty) != empty)
            return; // a transaction already contributed

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
