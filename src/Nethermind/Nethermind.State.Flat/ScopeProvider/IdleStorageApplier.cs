// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Logging;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// One process-wide thread that applies committed storage writes to early storage trees while the block executes.
/// It runs under SCHED_IDLE on Linux, never blocks the block thread, and is skipped by blocks processed back to back.
/// </summary>
internal sealed class IdleStorageApplier
{
    private const int SchedIdle = 5;
    private const int YieldsBeforeSleeping = 64;
    private const int SleepsBeforeParking = 50;

    // Settable for tests.
    internal static TimeSpan MinIdleGap { get; set; } = TimeSpan.FromMilliseconds(250);

    private static readonly Lock InstanceLock = new();
    private static IdleStorageApplier? _instance;

    private readonly ConcurrentQueue<FlatStorageTree> _queue = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ILogger _logger;
    // 1 while the thread is parked or about to park.
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
        // Either Park's last look at the queue sees this tree, or this sees the flag Park set before it.
        if (Volatile.Read(ref _parked) != 0 && Interlocked.Exchange(ref _parked, 0) != 0) _wake.Release();
    }

    /// <summary>Whether the thread is parked or about to park. For tests.</summary>
    internal bool IsParked => Volatile.Read(ref _parked) != 0;

    public void BlockCommitted() => Volatile.Write(ref _lastBlockCommit, Stopwatch.GetTimestamp());

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
                    // The scope was disposed mid-pass; its early work is dropped with it.
                    if (_logger.IsDebug) _logger.Debug($"Early storage apply pass ended by the disposal of its scope: {e.Message}");
                }
                catch (Exception e)
                {
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

        // A spare release only makes a later Park return early.
        Volatile.Write(ref _parked, 0);
    }

    private void LowerPriority()
    {
        // BENCH ONLY: BENCH_EARLY_APPLY_NORMAL=1 keeps this thread at normal priority (A/B of its CPU share).
        if (Environment.GetEnvironmentVariable("BENCH_EARLY_APPLY_NORMAL") == "1")
        {
            if (_logger.IsInfo) _logger.Info("BENCH: early storage apply thread left at normal priority");
            return;
        }

        // Best effort: an exception here, e.g. from resolving the libc import, would otherwise end the process.
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
