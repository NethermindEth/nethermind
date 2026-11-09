// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Paprika.Store;
using Paprika.Store.PageManagers;

namespace Nethermind.State.Flat.Persistence.PaprikaFlat;

/// <summary>
/// Bounds the resident memory of the memory-mapped PaprikaFlat file: tracks the pages of the mapping that are read and
/// releases the least recently used beyond its capacity with <c>madvise(MADV_DONTNEED)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the persisted-snapshot arena's residency advisor. The unit is a Paprika page, or the OS page where that is
/// larger, as <c>madvise</c> releases whole OS pages. A read fault can also map neighbours already in the page cache
/// (fault-around); they stay resident untracked until read, so the mapping can exceed the budget by them. A unit
/// overlapping a root page is never tracked, so never released.
/// </para>
/// <para>
/// A unit evicted from the tracker is queued for a drain thread, which keeps it if it was read again since; when the
/// queue is full, the reader releases it itself. Released dirty pages keep their data, as the mapping is shared. Nothing
/// refreshes the resident pages in the kernel's LRU: the mapping is advised <c>MADV_RANDOM</c>, under which reclaim
/// ignores their accessed bits since Linux 6.3.
/// </para>
/// </remarks>
/// <param name="tracker">The tracker of the resident units, which the advisor owns.</param>
/// <param name="historyDepth">The number of root pages of the store, which are never released.</param>
/// <param name="unitBytes">The tracking unit: a power of two, at least <see cref="Page.PageSize"/>.</param>
/// <param name="ringCapacity">The capacity of the drain queue: a power of two.</param>
/// <param name="logger">The logger.</param>
internal sealed unsafe partial class PaprikaPageResidencyAdvisor(PageResidencyTracker tracker, byte historyDepth, int unitBytes, int ringCapacity, ILogger logger)
    : IPageAccessObserver
{
    /// <summary>The tracking unit, in bytes.</summary>
    public static readonly int UnitBytes = Math.Max(Environment.SystemPageSize, Page.PageSize);

    private const int MinCapacity = 8;
    private const int MinRingCapacity = 8192;
    private const int MADV_DONTNEED = 4;
    private static readonly TimeSpan MetricsInterval = TimeSpan.FromSeconds(1);

    [LibraryImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static partial int Madvise(void* addr, nuint length, int advice);

    private readonly MpmcRingBuffer<uint> _ring = new(ringCapacity);
    private readonly int _unitShift = BitOperations.Log2((uint)unitBytes) - BitOperations.Log2((uint)Page.PageSize);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _drainCts = new();
    private Task? _drainTask;
    private Timer? _metricsTimer;
    private byte* _unitBase;
    private byte* _mapEnd;
    private uint _skewPages;
    // No unit is tracked until the mapping is known.
    private uint _firstTrackedUnit = uint.MaxValue;
    private bool _disposed;
    // 0 = the drain may sleep, 1 = a unit was queued since it last looked. Producers flip it to 1 and wake the drain;
    // the drain resets it, fenced against its reads of the queue, before draining and re-checks it after, so no wake-up
    // is lost.
    private int _signal;
    private long _queued;
    private long _inlineFallback;
    private long _skippedRetouched;
    private long _dispatched;
    private long _publishedInlineFallback;
    private long _publishedSkippedRetouched;
    private long _publishedDispatched;

    /// <summary>Creates an advisor that keeps at most <paramref name="budgetBytes"/> of a mapping resident.</summary>
    /// <param name="budgetBytes">The budget; the units it holds are rounded down to a power of two, with a minimum of 8.</param>
    /// <param name="historyDepth">The number of root pages of the store, which are never released.</param>
    /// <param name="logManager">The log manager.</param>
    /// <returns><c>null</c> when <paramref name="budgetBytes"/> is not positive, or off Linux, where releasing pages does nothing.</returns>
    public static PaprikaPageResidencyAdvisor? Create(long budgetBytes, byte historyDepth, ILogManager logManager)
    {
        if (budgetBytes <= 0 || !OperatingSystem.IsLinux()) return null;

        int capacity = 1 << BitOperations.Log2((ulong)Math.Clamp(budgetBytes / UnitBytes, MinCapacity, int.MaxValue));
        int ringCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(MinRingCapacity, capacity / 8));
        ILogger logger = logManager.GetClassLogger<PaprikaPageResidencyAdvisor>();
        if (logger.IsInfo) logger.Info($"PaprikaFlat resident memory budget is {budgetBytes} bytes: {capacity} units of {UnitBytes} bytes");
        return new PaprikaPageResidencyAdvisor(new PageResidencyTracker(capacity), historyDepth, UnitBytes, ringCapacity, logger);
    }

    /// <summary>The tracker of the resident units, keyed by unit index under id 0.</summary>
    public PageResidencyTracker Tracker => tracker;

    /// <summary>The capacity of the drain queue.</summary>
    public int RingCapacity => ringCapacity;

    /// <summary>Whether the drain thread is not running: never started, or stopped by <see cref="Dispose"/>.</summary>
    public bool DrainStopped => _drainTask?.IsCompleted ?? true;

    /// <summary>The bytes of the units tracked as resident.</summary>
    public long ResidentBytes => tracker.ResidentBytes / Environment.SystemPageSize * unitBytes;

    /// <summary>The evicted units queued for the drain.</summary>
    public long Queued => Volatile.Read(ref _queued);

    /// <summary>The evicted units a reader released itself, as the drain queue was full.</summary>
    public long InlineFallback => Volatile.Read(ref _inlineFallback);

    /// <summary>The queued units the drain kept, as they were read again since.</summary>
    public long SkippedRetouched => Volatile.Read(ref _skippedRetouched);

    /// <summary>The queued units the drain released.</summary>
    public long Dispatched => Volatile.Read(ref _dispatched);

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The advisor already observes a mapping.</exception>
    public void OnMapped(nint basePointer, long mappedBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_drainTask is not null) throw new InvalidOperationException("The advisor already observes a mapping.");

        _skewPages = (uint)((nuint)basePointer % (uint)unitBytes / Page.PageSize);
        _unitBase = (byte*)basePointer - _skewPages * Page.PageSize;
        _mapEnd = (byte*)basePointer + mappedBytes;
        _firstTrackedUnit = UnitOf(historyDepth - 1u) + 1;
        Metrics.PaprikaPageTrackerMetadataBytes = tracker.MetadataBytes;
        _drainTask = Task.Factory.StartNew(Drain, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _metricsTimer = new Timer(PublishMetrics, null, MetricsInterval, MetricsInterval);
    }

    /// <inheritdoc/>
    public void OnAccess(uint page)
    {
        uint unit = UnitOf(page);
        if (unit < _firstTrackedUnit || Volatile.Read(ref _disposed)) return;

        if (tracker.TryTouch(0, unit, out _, out uint victim) == PageResidencyTracker.TouchOutcome.Evicted) Queue(victim);
    }

    /// <summary>Stops releasing units and frees the tracker; a second call does nothing.</summary>
    /// <remarks>No <see cref="OnAccess"/> may run meanwhile, as the tracker it reads is freed; later ones do nothing.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true)) return;

        if (_metricsTimer is not null)
        {
            using ManualResetEvent stopped = new(false);
            if (_metricsTimer.Dispose(stopped)) stopped.WaitOne();
        }

        try
        {
            _drainCts.Cancel();
            _drainTask?.Wait();
        }
        catch (Exception exception)
        {
            if (logger.IsError) logger.Error("Stopping the PaprikaFlat page residency drain failed", exception);
        }

        // The units still queued are left: the mapping is unmapped next.
        PublishCounters();
        Metrics.PaprikaPageTrackerResidentBytes = 0;
        Metrics.PaprikaPageTrackerMetadataBytes = 0;
        Metrics.PaprikaPageTrackerEvictionQueueDepth = 0;
        tracker.Dispose();
        _wake.Dispose();
        _drainCts.Dispose();
    }

    private uint UnitOf(uint page) => (page + _skewPages) >> _unitShift;

    private void Queue(uint unit)
    {
        if (_ring.TryEnqueue(unit))
        {
            Interlocked.Increment(ref _queued);
            if (Interlocked.Exchange(ref _signal, 1) == 0) _wake.Release();
            return;
        }

        Interlocked.Increment(ref _inlineFallback);
        Release(unit);
    }

    private void Drain()
    {
        CancellationToken token = _drainCts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _signal, 0);
                while (!token.IsCancellationRequested && _ring.TryDequeue(out uint unit)) DispatchOne(unit);
                if (Volatile.Read(ref _signal) == 0) _wake.Wait(token);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    private void DispatchOne(uint unit)
    {
        // Released now, a unit read again since it was queued would only fault back in.
        if (tracker.ContainsPage(0, unit))
        {
            Interlocked.Increment(ref _skippedRetouched);
            return;
        }

        Interlocked.Increment(ref _dispatched);
        Release(unit);
    }

    private void Release(uint unit)
    {
        // Tracked units start after the root pages, so within the mapping; only the last can extend past its end.
        byte* start = _unitBase + (nuint)unit * (uint)unitBytes;
        if (start < _mapEnd) Madvise(start, (nuint)Math.Min(unitBytes, _mapEnd - start), MADV_DONTNEED);
    }

    private void PublishMetrics(object? state)
    {
        Metrics.PaprikaPageTrackerResidentBytes = ResidentBytes;
        Metrics.PaprikaPageTrackerEvictionQueueDepth = _ring.EstimatedJobCount;
        PublishCounters();
    }

    // The metrics take deltas, so they keep counting across advisors, whose counters each start at zero.
    private void PublishCounters()
    {
        PublishDelta(ref Metrics._paprikaPageTrackerEvictionsDispatched, Dispatched, ref _publishedDispatched);
        PublishDelta(ref Metrics._paprikaPageTrackerEvictionsInlineFallback, InlineFallback, ref _publishedInlineFallback);
        PublishDelta(ref Metrics._paprikaPageTrackerEvictionsSkippedRetouched, SkippedRetouched, ref _publishedSkippedRetouched);
    }

    private static void PublishDelta(ref long metric, long counter, ref long published) =>
        Interlocked.Add(ref metric, counter - Interlocked.Exchange(ref published, counter));
}
