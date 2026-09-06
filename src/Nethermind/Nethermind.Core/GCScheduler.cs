// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
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

    // Flag indicating if a garbage collection is currently in progress or disallowed
    private static int _canPerformGC = CanPerformGC;

    // Timer for scheduling periodic garbage collections when idle
    private readonly Timer _gcTimer;
    private readonly Timer? _sustainedSweepTimer;
    private readonly MallocHelper _mallocHelper;
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

    // Singleton instance of GCScheduler
    public static GCScheduler Instance { get; } = new GCScheduler();

    private GCScheduler() : this(sustainedSweepEnabled: true, MallocHelper.Instance)
    {
    }

    // Test ctor: a private instance without the sweep timer cannot race assertions on its state.
    internal GCScheduler(bool sustainedSweepEnabled, MallocHelper? mallocHelper = null)
    {
        _mallocHelper = mallocHelper ?? MallocHelper.Instance;
        // Initialize the timer without starting it
        _gcTimer = new Timer(_ => PerformFullGC(), null, Timeout.Infinite, Timeout.Infinite);
        if (sustainedSweepEnabled)
        {
            _sustainedSweepTimer = new Timer(_ => SweepIfAllocationBudgetExceeded(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
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
    public bool GCCollect(int generation, GCCollectionMode mode, bool blocking, bool compacting) =>
        GCCollect(generation, mode, blocking, compacting, trimNativeMemory: true);

    private bool GCCollect(int generation, GCCollectionMode mode, bool blocking, bool compacting, bool trimNativeMemory)
    {
        if (Volatile.Read(ref _forcedGCExclusions) > 0)
        {
            return false;
        }

        if (!MarkGCPaused())
        {
            // Skip if another GC is in progress
            return false;
        }

        // Reset the block counter after GC
        _countToGC = MaxBlocksWithoutGC;
        // Scheduler-issued gen2 restarts the sustained-sweep budget; runtime-initiated ones don't.
        if (generation >= GC.MaxGeneration)
        {
            Volatile.Write(ref _sweepBaselineAllocatedBytes, GC.GetTotalAllocatedBytes(precise: false));
        }
        System.GC.Collect(generation, mode, blocking: blocking, compacting: compacting);
        if (trimNativeMemory)
        {
            // Also trim native memory used by Db
            _mallocHelper.MallocTrim((uint)1.MiB);
        }
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
        if (allocated - Volatile.Read(ref _sweepBaselineAllocatedBytes) < SustainedSweepAllocationBytes) return;

        GCCollect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false, trimNativeMemory: false);
    }

    internal long SweepBaselineAllocatedBytes
    {
        get => Volatile.Read(ref _sweepBaselineAllocatedBytes);
        set => Volatile.Write(ref _sweepBaselineAllocatedBytes, value);
    }
}
