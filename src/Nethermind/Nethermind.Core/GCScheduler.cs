// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;

namespace Nethermind.Core;

public sealed class GCScheduler
{
    private const int CanPerformGC = 0;
    private const int GCNotAllowed = 1;
    // Thresholds and limits for triggering garbage collection
    private const int BlocksBacklogTriggeringManualGC = 4;
    private const int MaxBlocksWithoutGC = 250;
    private const int MinSecondsBetweenForcedGC = 120;
    // 4 GiB ≈ 256 typical 30 MGas mainnet blocks
    internal const long SustainedSweepAllocationBytes = 4L * 1024 * 1024 * 1024;
    // Far above any observed background gen2 duration; only guards against a missed GCEnd event.
    private const long BackgroundGCTimeoutMs = 300_000;

    // Flag indicating if a garbage collection is currently in progress or disallowed
    private static int _canPerformGC = CanPerformGC;

    // Timer for scheduling periodic garbage collections when idle
    private readonly Timer _gcTimer;
    private readonly Timer? _sustainedSweepTimer;
    private readonly Stopwatch _stopwatch = new();
    private Task _lastGcTask = Task.CompletedTask;
    private bool _isNextGcBlocking = false;
    private bool _isNextGcCompacting = false;
    private bool _gcTimerSet = false;
    private bool _fireGC = false;
    private long _countToGC = 0L;

    private bool _skipNextGC = false;
    private long _sweepBaselineAllocatedBytes;
    private int _forcedGCExclusions;

    // TickCount64 when the in-flight background gen2 started; -1 = none. Written by the tracker.
    private static long _backgroundGCStartedAtMs = -1;

    // Rooted for the process lifetime; keeps _backgroundGCStartedAtMs current.
    private readonly BackgroundGCTracker? _backgroundGCTracker;

    // DIAG: env-tunable sweep budget (bytes); 0 disables the sweep. Not for production.
    private static readonly long _sustainedSweepBudget =
        long.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_SWEEP_BUDGET_BYTES"), out long bytes)
            ? bytes
            : SustainedSweepAllocationBytes;

    // DIAG: last logged gen2 GC indices for the watcher timer.
    private long _lastBgcIndex;
    private long _lastFullBlockingIndex;
    private readonly Timer? _gcDiagTimer;

    // Singleton instance of GCScheduler
    public static GCScheduler Instance { get; } = new GCScheduler();

    private GCScheduler() : this(sustainedSweepEnabled: true)
    {
    }

    // Test ctor: a private instance without the sweep timer cannot race assertions on its state.
    internal GCScheduler(bool sustainedSweepEnabled)
    {
        // Initialize the timer without starting it
        _gcTimer = new Timer(_ => PerformFullGC(), null, Timeout.Infinite, Timeout.Infinite);
        if (sustainedSweepEnabled && _sustainedSweepBudget > 0)
        {
            _backgroundGCTracker = new BackgroundGCTracker();
            _sustainedSweepTimer = new Timer(_ => SweepIfAllocationBudgetExceeded(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        if (sustainedSweepEnabled)
        {
            Console.WriteLine($"[GCDIAG] sweep budget bytes={_sustainedSweepBudget}");
            _gcDiagTimer = new Timer(_ => LogGen2Collections(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        }
    }

    // DIAG: logs every gen2 GC (scheduler- or runtime-initiated) with its STW pause segments.
    private void LogGen2Collections()
    {
        LogGen2Info(GCKind.Background, ref _lastBgcIndex);
        LogGen2Info(GCKind.FullBlocking, ref _lastFullBlockingIndex);
    }

    private static void LogGen2Info(GCKind kind, ref long lastIndex)
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo(kind);
        if (info.Index == 0 || info.Index == lastIndex) return;
        lastIndex = info.Index;

        System.Text.StringBuilder pauses = new();
        foreach (TimeSpan pause in info.PauseDurations)
        {
            if (pauses.Length > 0) pauses.Append('+');
            pauses.Append(pause.TotalMilliseconds.ToString("F0"));
        }

        Console.WriteLine(
            $"[GCDIAG] {DateTime.UtcNow:HH:mm:ss.fff} gen2 kind={kind} index={info.Index} gen={info.Generation} " +
            $"pausesMs={pauses} pausePct={info.PauseTimePercentage:F1} heapGB={info.HeapSizeBytes / 1e9:F1} " +
            $"promotedMB={info.PromotedBytes / 1e6:F0} pinned={info.PinnedObjectsCount} " +
            $"compacted={info.Compacted} memLoadGB={info.MemoryLoadBytes / 1e9:F1} " +
            $"highThreshGB={info.HighMemoryLoadThresholdBytes / 1e9:F1} availGB={info.TotalAvailableMemoryBytes / 1e9:F1} " +
            $"totalPauseS={GC.GetTotalPauseDuration().TotalSeconds:F1}");
    }

    /// <summary>
    /// Tracks whether a background gen2 collection is currently running via runtime GCStart/GCEnd events.
    /// </summary>
    /// <remarks>
    /// <see cref="GC.GetGCMemoryInfo(GCKind)"/> cannot provide this: its background record is published
    /// at the final-mark pause while the concurrent sweep phase keeps running for tens of seconds.
    /// The GCEnd event for the background collection's count fires only at true completion.
    /// </remarks>
    private sealed class BackgroundGCTracker : EventListener
    {
        private const EventKeywords GCKeyword = (EventKeywords)0x1;
        private const int GCStartEventId = 1;
        private const int GCEndEventId = 2;
        private const uint BackgroundGCType = 1;

        private long _backgroundGCCount = -1;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            Console.WriteLine($"[GCDIAG] event source created: {eventSource.Name}");
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(eventSource, EventLevel.Informational, GCKeyword);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            // GCStart payload: Count, Depth, Reason, Type; GCEnd payload: Count, Depth
            if (eventData.EventId == GCStartEventId)
            {
                if (eventData.Payload is { Count: >= 4 } payload
                    && payload[3] is uint type && type == BackgroundGCType
                    && payload[0] is uint count)
                {
                    _backgroundGCCount = count;
                    Volatile.Write(ref _backgroundGCStartedAtMs, Environment.TickCount64);
                    Console.WriteLine($"[GCDIAG] {DateTime.UtcNow:HH:mm:ss.fff} bgc start count={count}");
                }
            }
            else if (eventData.EventId == GCEndEventId)
            {
                if (eventData.Payload is { Count: >= 1 } payload
                    && payload[0] is uint count && count == _backgroundGCCount)
                {
                    long startedAt = Volatile.Read(ref _backgroundGCStartedAtMs);
                    Volatile.Write(ref _backgroundGCStartedAtMs, -1);
                    Console.WriteLine($"[GCDIAG] {DateTime.UtcNow:HH:mm:ss.fff} bgc end count={count} durMs={(startedAt >= 0 ? Environment.TickCount64 - startedAt : -1)}");
                }
            }
        }
    }

    /// <summary>
    /// Activates background garbage collection when the processing queue is idle.
    /// </summary>
    /// <param name="queueCount">Number of items in the processing queue.</param>
    public void SwitchOnBackgroundGC(int queueCount)
    {
        if (_fireGC)
        {
            _countToGC--;
            // Trigger GC if block limit reached and minimum time elapsed
            if (_countToGC <= 0 && _stopwatch.Elapsed.TotalSeconds > MinSecondsBetweenForcedGC)
            {
                _fireGC = false;
                _stopwatch.Reset();
                // Ensure only one GC task runs at a time
                if (_lastGcTask.IsCompleted)
                {
                    _lastGcTask = PerformFullGCAsync();
                }
            }
        }

        // Avoid setting the timer if there are items in the queue or timer is already set
        if (queueCount <= 0 && !_gcTimerSet)
        {
            _gcTimerSet = true;
            // Schedule GC every 2 minutes when idle
            _gcTimer.Change(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
        }
    }

    /// <summary>
    /// Deactivates background garbage collection when the processing queue is active.
    /// </summary>
    /// <param name="queueCount">Number of items in the processing queue.</param>
    public void SwitchOffBackgroundGC(int queueCount)
    {
        if (!_fireGC && queueCount > BlocksBacklogTriggeringManualGC)
        {
            // Long chains in archive sync don't force GC and don't call MallocTrim
            // Start manual GC due to high backlog
            _fireGC = true;
            _countToGC = MaxBlocksWithoutGC;
            _stopwatch.Restart();
        }
        else if (queueCount == 0)
        {
            // Stop manual GC when queue is empty
            _fireGC = false;
        }

        if (_gcTimerSet)
        {
            // Stop the GC timer as the system is no longer idle
            _gcTimerSet = false;
            _gcTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Performs a full garbage collection asynchronously to prevent blocking the main thread.
    /// </summary>
    private async Task PerformFullGCAsync()
    {
        // Switch to a background thread
        await Task.Yield();

        PerformFullGC();
    }

    /// <summary>
    /// Marks that a garbage collection is in progress; or one shouldn't happen.
    /// </summary>
    /// <returns>True if marking succeeded; false if a GC is already in progress.</returns>
    public static bool MarkGCPaused() => Interlocked.CompareExchange(ref _canPerformGC, GCNotAllowed, CanPerformGC) == CanPerformGC;

    /// <summary>
    /// Marks that garbage collection has finished.
    /// </summary>
    public static void MarkGCResumed() => Volatile.Write(ref _canPerformGC, CanPerformGC);

    /// <summary>
    /// Determines and performs the appropriate type of garbage collection.
    /// </summary>
    private void PerformFullGC()
    {
        if (Interlocked.Exchange(ref _skipNextGC, false))
        {
            return;
        }

        // Decide if the next GC should compact the large object heap
        bool compacting = _isNextGcBlocking && _isNextGcCompacting;

        // Default to collecting generation 1
        int generation = 1;
        GCCollectionMode mode = GCCollectionMode.Forced;
        if (compacting)
        {
            // Collect all generations
            generation = GC.MaxGeneration;
            // Compact large object heap
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            // Release memory back to the OS
            mode = GCCollectionMode.Aggressive;
        }

        // Attempt to perform garbage collection
        if (GCCollect(generation, mode, blocking: _isNextGcBlocking, compacting: compacting))
        {
            if (_isNextGcBlocking)
            {
                // Toggle compacting mode for the next blocking GC
                _isNextGcCompacting = !_isNextGcCompacting;
            }
            // Alternate between blocking and non-blocking GCs
            _isNextGcBlocking = !_isNextGcBlocking;
        }
    }

    /// <summary>
    /// Executes garbage collection with specified parameters.
    /// </summary>
    /// <param name="generation">The oldest generation to collect.</param>
    /// <param name="mode">The garbage collection mode.</param>
    /// <param name="blocking">Whether the GC should be blocking.</param>
    /// <param name="compacting">Whether the GC should compact the large object heap.</param>
    /// <returns>True if GC was performed; false if another GC was in progress or forced collections are excluded (e.g. during pruning).</returns>
    public bool GCCollect(int generation, GCCollectionMode mode, bool blocking, bool compacting)
    {
        if (Volatile.Read(ref _forcedGCExclusions) > 0)
        {
            Console.WriteLine($"[GCDIAG] {DateTime.UtcNow:HH:mm:ss.fff} GCCollect gen={generation} mode={mode} blocking={blocking} compacting={compacting} -> excluded");
            return false;
        }

        if (!MarkGCPaused())
        {
            // Skip if another GC is in progress
            Console.WriteLine($"[GCDIAG] {DateTime.UtcNow:HH:mm:ss.fff} GCCollect gen={generation} mode={mode} blocking={blocking} compacting={compacting} -> guard held");
            return false;
        }

        Console.WriteLine($"[GCDIAG] {DateTime.UtcNow:HH:mm:ss.fff} GCCollect gen={generation} mode={mode} blocking={blocking} compacting={compacting} -> running (lohMode={GCSettings.LargeObjectHeapCompactionMode})");

        // Reset the block counter after GC
        _countToGC = MaxBlocksWithoutGC;
        // Scheduler-issued gen2 restarts the sustained-sweep budget; runtime-initiated ones don't.
        if (generation >= GC.MaxGeneration)
        {
            Volatile.Write(ref _sweepBaselineAllocatedBytes, GC.GetTotalAllocatedBytes(precise: false));
        }
        System.GC.Collect(generation, mode, blocking: blocking, compacting: compacting);
        // Also trim native memory used by Db
        long trimStart = Environment.TickCount64;
        MallocHelper.Instance.MallocTrim((uint)1.MiB);
        long trimMs = Environment.TickCount64 - trimStart;
        if (trimMs > 50) Console.WriteLine($"[GCDIAG] {DateTime.UtcNow:HH:mm:ss.fff} mallocTrim wallMs={trimMs}");
        // Indicate that GC has finished
        MarkGCResumed();

        return true;
    }

    public void SkipNextGC() => Volatile.Write(ref _skipNextGC, true);

    /// <summary>Excludes forced collections for the scope's lifetime (e.g. while pruning).</summary>
    public ForcedGCExclusionScope ExcludeForcedGC()
    {
        Interlocked.Increment(ref _forcedGCExclusions);
        return new ForcedGCExclusionScope(this);
    }

    public readonly struct ForcedGCExclusionScope(GCScheduler scheduler) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref scheduler._forcedGCExclusions);
    }

    // Keeps gen2 small when blocks stream back-to-back and the idle-window sweeps never engage,
    // preventing the runtime's multi-second blocking gen2 escalation.
    internal void SweepIfAllocationBudgetExceeded()
    {
        long allocated = GC.GetTotalAllocatedBytes(precise: false);
        long sinceBaseline = allocated - Volatile.Read(ref _sweepBaselineAllocatedBytes);
        if (sinceBaseline < _sustainedSweepBudget) return;

        // An induced gen2 while a background collection is still in flight is escalated by the
        // runtime to a full blocking collection (observed 1-2s stop-the-world on replay-sized
        // heaps). Stay armed and retry on a later tick instead; the budget check above keeps firing.
        if (IsBackgroundGCInFlight())
        {
            Console.WriteLine($"[GCDIAG] {DateTime.UtcNow:HH:mm:ss.fff} sweep deferred: bgc in flight since {GCScheduler.BackgroundGCStartedAtMs} allocGB={sinceBaseline / 1e9:F1}");
            return;
        }

        GCMemoryInfo before = GC.GetGCMemoryInfo();
        long pauseBeforeMs = (long)GC.GetTotalPauseDuration().TotalMilliseconds;
        long start = Environment.TickCount64;
        bool fired = GCCollect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
        long wallMs = Environment.TickCount64 - start;
        long pauseDeltaMs = (long)GC.GetTotalPauseDuration().TotalMilliseconds - pauseBeforeMs;
        Console.WriteLine(
            $"[GCDIAG] {DateTime.UtcNow:HH:mm:ss.fff} sweep fired={fired} wallMs={wallMs} " +
            $"pauseDeltaMs={pauseDeltaMs} allocGB={sinceBaseline / 1e9:F1} " +
            $"memLoadGB={before.MemoryLoadBytes / 1e9:F1} highThreshGB={before.HighMemoryLoadThresholdBytes / 1e9:F1} " +
            $"lohMode={GCSettings.LargeObjectHeapCompactionMode}");
    }

    private static bool IsBackgroundGCInFlight()
    {
        long startedAt = Volatile.Read(ref _backgroundGCStartedAtMs);
        return startedAt >= 0 && Environment.TickCount64 - startedAt <= BackgroundGCTimeoutMs;
    }

    internal long SweepBaselineAllocatedBytes
    {
        get => Volatile.Read(ref _sweepBaselineAllocatedBytes);
        set => Volatile.Write(ref _sweepBaselineAllocatedBytes, value);
    }

    internal static long BackgroundGCStartedAtMs
    {
        get => Volatile.Read(ref _backgroundGCStartedAtMs);
        set => Volatile.Write(ref _backgroundGCStartedAtMs, value);
    }
}
