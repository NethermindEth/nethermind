// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// One process-wide thread that applies committed storage writes to early storage trees while the block executes.
/// It runs under SCHED_IDLE on Linux unless configured otherwise, never blocks the block thread, and is skipped by blocks
/// processed back to back.
/// </summary>
internal sealed class IdleStorageApplier
{
    private const int SchedOther = 0;
    private const int SchedIdle = 5;
    private const int LowNice = 10;
    private const int NormalNice = 0;
    private const int YieldsBeforeSleeping = 64;
    private const int SleepsBeforeParking = 50;

    // Settable for tests.
    internal static TimeSpan MinIdleGap { get; set; } = TimeSpan.FromMilliseconds(250);

    private static readonly Lock InstanceLock = new();
    private static IdleStorageApplier? _instance;

    private readonly ConcurrentQueue<FlatStorageTree> _queue = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ILogger _logger;
    private readonly IThreadSchedulingCalls _scheduling;
    // 1 while the thread is parked or about to park.
    private int _parked;
    private long _lastBlockCommit;

    internal IdleStorageApplier(ILogManager logManager, EarlyStorageApplyPriority priority, IThreadSchedulingCalls scheduling)
    {
        _logger = logManager.GetClassLogger<IdleStorageApplier>();
        Priority = priority;
        _scheduling = scheduling;
        Thread thread = new(Run) { IsBackground = true, Name = "Storage early apply" };
        thread.Start();
    }

    /// <summary>The priority the thread was started at.</summary>
    internal EarlyStorageApplyPriority Priority { get; }

    /// <summary>
    /// The process-wide applier. The first call starts its thread at <paramref name="priority"/>; later calls get that
    /// same thread whatever priority they pass.
    /// </summary>
    public static IdleStorageApplier GetInstance(ILogManager logManager, EarlyStorageApplyPriority priority)
    {
        IdleStorageApplier? instance = Volatile.Read(ref _instance);
        if (instance is not null) return instance;

        lock (InstanceLock)
        {
            return _instance ??= new IdleStorageApplier(logManager, priority, ThreadSchedulingCalls.Instance);
        }
    }

    /// <summary>
    /// Puts <paramref name="instance"/> in place of the process-wide applier until disposed; with null, the next
    /// <see cref="GetInstance"/> starts a thread of its own. For tests.
    /// </summary>
    internal static IDisposable ReplaceInstance(IdleStorageApplier? instance)
    {
        IdleStorageApplier? previous;
        lock (InstanceLock)
        {
            previous = _instance;
            _instance = instance;
        }

        return new Reactive.AnonymousDisposable(() =>
        {
            lock (InstanceLock) _instance = previous;
        });
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
        string scheduling = SetPriority(Priority, _scheduling, _logger);
        if (_logger.IsInfo) _logger.Info($"Early storage apply thread runs at {Priority} priority: {scheduling}");

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

    /// <summary>Sets the scheduling of the calling thread for <paramref name="priority"/> and describes what it got.</summary>
    internal static string SetPriority(EarlyStorageApplyPriority priority, IThreadSchedulingCalls calls, ILogger logger)
    {
        ThreadPriority fallback = priority switch
        {
            EarlyStorageApplyPriority.Low => ThreadPriority.BelowNormal,
            EarlyStorageApplyPriority.Normal => ThreadPriority.Normal,
            _ => ThreadPriority.Lowest
        };

        // Best effort: an exception here, e.g. from resolving the libc import, would otherwise end the process.
        try
        {
            if (calls.IsLinux)
            {
                string? policy = priority switch
                {
                    EarlyStorageApplyPriority.Low => TrySetOtherPolicy(LowNice, calls, logger),
                    EarlyStorageApplyPriority.Normal => TrySetOtherPolicy(NormalNice, calls, logger),
                    _ => TrySetIdlePolicy(calls, logger)
                };
                if (policy is not null) return policy;
            }

            calls.SetManagedPriority(fallback);
            return $"thread priority {fallback}";
        }
        catch (Exception e)
        {
            if (logger.IsWarn) logger.Warn($"Could not set the priority of the early storage apply thread, so it keeps the one it started with: {e.Message}");
            return "the priority it started with";
        }
    }

    private static string? TrySetIdlePolicy(IThreadSchedulingCalls calls, ILogger logger)
    {
        if (calls.TrySetSchedulerPolicy(SchedIdle, out int errno)) return "SCHED_IDLE";
        if (logger.IsWarn) logger.Warn($"Could not move the early storage apply thread to SCHED_IDLE (errno {errno}); lowering its priority instead.");
        return null;
    }

    private static string? TrySetOtherPolicy(int nice, IThreadSchedulingCalls calls, ILogger logger)
    {
        if (!calls.TrySetSchedulerPolicy(SchedOther, out int errno))
        {
            if (logger.IsWarn) logger.Warn($"Could not move the early storage apply thread to SCHED_OTHER (errno {errno}); setting its thread priority instead.");
            return null;
        }

        int tid = calls.GetThreadId();
        if (tid <= 0)
        {
            if (logger.IsWarn) logger.Warn("Could not read the kernel id of the early storage apply thread; setting its thread priority instead.");
            return null;
        }

        if (!calls.TrySetThreadNice(tid, nice, out errno))
        {
            if (logger.IsWarn) logger.Warn($"Could not set nice {nice} for the early storage apply thread {tid} (errno {errno}); setting its thread priority instead.");
            return null;
        }

        return $"SCHED_OTHER nice {nice}, tid {tid}";
    }
}
