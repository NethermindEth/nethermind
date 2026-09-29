// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Logging;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// One process-wide background thread that applies transactions' committed storage writes to the storage tries while
/// the rest of the block executes, using only CPU time no other thread wants.
/// </summary>
/// <remarks>
/// On Linux the thread runs under SCHED_IDLE, so it only gets a core the scheduler would otherwise leave idle. Its work
/// never takes a lock the block thread waits on: work arrives through a lock-free queue, and each storage tree hands
/// itself to the block-end write batch with a single atomic exchange, so a preempted apply is abandoned rather than
/// waited for.
/// <para>
/// Out of work, the thread yields for a moment, then sleeps 1 ms at a time, and after about 50 ms without work parks on
/// a semaphore until the next tree is queued. So it stays asleep between blocks and through blocks the gate below keeps
/// off it. The block thread releases the semaphore only for a parked thread, about once per block rather than once per
/// transaction, and the thread holds the semaphore's lock only for the moment it takes to block or wake.
/// </para>
/// <para>
/// A block only uses the thread when it follows an idle gap. Processed back to back, as in sync or catch-up, every core
/// is already busy through the whole block, and the thread's work slows execution by more than it saves at the end.
/// </para>
/// </remarks>
internal sealed class IdleStorageApplier
{
    private const int SchedIdle = 5;
    private const int YieldsBeforeSleeping = 64;
    // About 50 ms: longer than the gaps between a block's transactions, and short against the time between blocks.
    private const int SleepsBeforeParking = 50;

    /// <summary>Time since the previous block committed for a block to count as not processed back to back.</summary>
    /// <remarks>Settable for tests, which share the one thread.</remarks>
    internal static TimeSpan MinIdleGap { get; set; } = TimeSpan.FromMilliseconds(250);

    private static readonly Lock InstanceLock = new();
    private static IdleStorageApplier? _instance;

    private readonly ConcurrentQueue<FlatStorageTree> _queue = new();
    // What the parked thread waits on.
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ILogger _logger;
    // Set by the thread before it looks at the queue one last time and parks; the first Enqueue to see it wakes it.
    private int _parked;
    private long _lastBlockCommit;

    private IdleStorageApplier(ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<IdleStorageApplier>();
        Thread thread = new(Run) { IsBackground = true, Name = "Storage early apply" };
        thread.Start();
    }

    public static IdleStorageApplier GetInstance(ILogManager logManager)
    {
        IdleStorageApplier? instance = Volatile.Read(ref _instance);
        if (instance is not null) return instance;

        lock (InstanceLock)
        {
            return _instance ??= new IdleStorageApplier(logManager);
        }
    }

    public void Enqueue(FlatStorageTree storageTree)
    {
        _queue.Enqueue(storageTree);
        // Enqueue reserves the tree's slot with an interlocked operation, which orders it before this read, and Park
        // sets the flag with one before its last look at the queue: either Park sees the tree or this sees the flag.
        if (Volatile.Read(ref _parked) != 0 && Interlocked.Exchange(ref _parked, 0) != 0) _wake.Release();
    }

    /// <summary>Whether the thread is parked or about to park. For tests.</summary>
    internal bool IsParked => Volatile.Read(ref _parked) != 0;

    /// <summary>Records that a block processing scope committed a block.</summary>
    public void BlockCommitted() => Volatile.Write(ref _lastBlockCommit, Stopwatch.GetTimestamp());

    /// <summary>Whether a block starting now follows an idle gap since the previous block committed.</summary>
    public bool FollowsIdleGap()
    {
        long lastCommit = Volatile.Read(ref _lastBlockCommit);
        return lastCommit == 0 || Stopwatch.GetElapsedTime(lastCommit) >= MinIdleGap;
    }

    private void Run()
    {
        LowerPriority();

        int idleRounds = 0;
        while (true)
        {
            if (_queue.TryDequeue(out FlatStorageTree? storageTree))
            {
                idleRounds = 0;
                try
                {
                    storageTree.ApplyEarlyWrites();
                }
                catch (ObjectDisposedException e)
                {
                    // The block's scope was disposed mid-pass; its early work is discarded with it.
                    if (_logger.IsDebug) _logger.Debug($"Early storage apply pass ended by the disposal of its scope: {e.Message}");
                }
                catch (Exception e)
                {
                    // ApplyEarlyWrites discards the tree's early state before rethrowing, so the block-end batch
                    // writes every slot as it would without this thread.
                    if (_logger.IsWarn) _logger.Warn($"Early storage apply failed and was discarded: {e}");
                }

                continue;
            }

            if (++idleRounds < YieldsBeforeSleeping) Thread.Yield();
            else if (idleRounds < YieldsBeforeSleeping + SleepsBeforeParking) Thread.Sleep(1);
            else
            {
                Park();
                idleRounds = 0;
            }
        }
    }

    private void Park()
    {
        Interlocked.Exchange(ref _parked, 1);
        if (_queue.IsEmpty) _wake.Wait();

        // Already cleared by the Enqueue that woke the thread. When the queue was not empty after all, an Enqueue may
        // still claim the wake-up and leave the semaphore one count ahead, which only makes a later Park return early.
        Volatile.Write(ref _parked, 0);
    }

    private void LowerPriority()
    {
        // Unhandled, an exception here would end the process: the libc import, for one, is only resolved by the call.
        // If one is thrown, the thread carries on at normal priority.
        try
        {
            if (OperatingSystem.IsLinux())
            {
                SchedParam param = default;
                // pid 0 is the calling thread.
                if (sched_setscheduler(0, SchedIdle, ref param) == 0) return;
                if (_logger.IsWarn) _logger.Warn($"Could not move the early storage apply thread to SCHED_IDLE (errno {Marshal.GetLastPInvokeError()}); lowering its priority instead.");
            }

            Thread.CurrentThread.Priority = ThreadPriority.Lowest;
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not lower the priority of the early storage apply thread, so it runs at normal priority: {e.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SchedParam
    {
        public int SchedPriority;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setscheduler(int pid, int policy, ref SchedParam param);
}
