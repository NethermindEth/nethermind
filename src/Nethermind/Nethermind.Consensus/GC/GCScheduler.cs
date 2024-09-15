// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Consensus;

public sealed class GCScheduler
{
    private const int BlocksBacklogTriggeringManualGC = 4;
    private const int MaxBlocksWithoutGC = 250;
    private const int MinSecondsBetweenForcedGC = 120;

    private static int _isPerformingGC = 0;

    private readonly Timer _gcTimer;
    private Task _lastGcTask = Task.CompletedTask;
    private bool _isNextGcBlocking = false;
    private bool _isNextGcCompacting = false;
    private bool _gcTimerSet = false;
    private bool _fireGC = false;
    private long _countToGC = 0L;
    private Stopwatch _stopwatch = new();

    public static GCScheduler Instance { get; } = new GCScheduler();

    private GCScheduler()
    {
        _gcTimer = new Timer(_ => PerformFullGC(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void SwitchOnBackgroundGC(int queueCount)
    {
        if (_fireGC)
        {
            _countToGC--;
            if (_countToGC <= 0 && _stopwatch.Elapsed.TotalSeconds > MinSecondsBetweenForcedGC)
            {
                _fireGC = false;
                _stopwatch.Reset();
                if (_lastGcTask.IsCompleted)
                {
                    _lastGcTask = PerformFullGCAsync();
                }
            }
        }

        if (queueCount > 0 || _gcTimerSet)
        {
            // Don't switch on if still processing blocks
            return;
        }
        _gcTimerSet = true;
        // GC every 2 minutes if block processing idle
        _gcTimer.Change(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    public void SwitchOffBackgroundGC(int queueCount)
    {
        if (!_fireGC && queueCount > BlocksBacklogTriggeringManualGC)
        {
            // Long chains in archive sync don't force GC and don't call MallocTrim;
            // so we trigger it manually
            _fireGC = true;
            _countToGC = MaxBlocksWithoutGC;
            _stopwatch.Restart();
        }
        else if (queueCount == 0)
        {
            // Nothing remaining in the queue, so we can stop forcing GC
            _fireGC = false;
        }

        if (!_gcTimerSet)
        {
            return;
        }
        _gcTimerSet = false;
        _gcTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public async Task PerformFullGCAsync()
    {
        // Flip to ThreadPool to avoid blocking the main processing thread
        await Task.Yield();

        PerformFullGC();
    }

    public static bool MarkGCPaused()
    {
        return Interlocked.CompareExchange(ref _isPerformingGC, 1, 0) == 0;
    }

    public static void MarkGCResumed()
    {
        Volatile.Write(ref _isPerformingGC, 0);
    }

    private void PerformFullGC()
    {
        // Compacting GC every other cycle of blocking GC
        bool compacting = _isNextGcBlocking && _isNextGcCompacting;

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

        if (!GCCollect(generation, mode, blocking: _isNextGcBlocking, compacting: compacting))
        {
            return;
        }

        if (_isNextGcBlocking)
        {
            // Switch compacting every other cycle of blocking GC
            _isNextGcCompacting = !_isNextGcCompacting;
        }
        _isNextGcBlocking = !_isNextGcBlocking;
    }

    public bool GCCollect(int generation, GCCollectionMode mode, bool blocking, bool compacting)
    {
        if (!MarkGCPaused())
        {
            // Skip if another GC is in progress
            return false;
        }

        _countToGC = MaxBlocksWithoutGC;
        System.GC.Collect(generation, mode, blocking: blocking, compacting: compacting);

        // Mark GC as finished
        MarkGCResumed();

        return true;
    }
}
