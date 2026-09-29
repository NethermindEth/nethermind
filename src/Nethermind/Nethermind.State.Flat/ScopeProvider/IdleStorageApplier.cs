// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Nethermind.Logging;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// One process-wide background thread that applies transactions' committed storage writes to the storage tries while
/// the rest of the block executes, using only CPU time no other thread wants.
/// </summary>
/// <remarks>
/// On Linux the thread runs under SCHED_IDLE, so it only gets a core the scheduler would otherwise leave idle. It never
/// takes a lock the block thread waits on: work arrives through a lock-free queue, and each storage tree hands itself
/// to the block-end write batch with a single atomic exchange, so a preempted apply is abandoned rather than waited for.
/// </remarks>
internal sealed class IdleStorageApplier
{
    private const int SchedIdle = 5;
    private const int YieldsBeforeSleeping = 64;

    private static readonly Lock InstanceLock = new();
    private static IdleStorageApplier? _instance;

    private readonly ConcurrentQueue<FlatStorageTree> _queue = new();
    private readonly ILogger _logger;

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

    public void Enqueue(FlatStorageTree storageTree) => _queue.Enqueue(storageTree);

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
                catch (Exception e)
                {
                    // ApplyEarlyWrites discards the tree's early state before rethrowing, so the block-end batch
                    // writes every slot as it would without this thread.
                    if (_logger.IsWarn) _logger.Warn($"Early storage apply failed and was discarded: {e}");
                }

                continue;
            }

            if (++idleRounds < YieldsBeforeSleeping) Thread.Yield();
            else Thread.Sleep(1);
        }
    }

    private void LowerPriority()
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SchedParam
    {
        public int SchedPriority;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setscheduler(int pid, int policy, ref SchedParam param);
}
