// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.IlEvm;

public static unsafe partial class IlEvm
{
    /// <summary>The Legacy rules' background compiler of layouts and chunks, on <see cref="IlEvmSettings.CompileThreads"/> threads, in <see cref="IlEvmSettings.CompileOrderHot"/> order.</summary>
    internal static class IlCompileQueue
    {
        private abstract class Job
        {
            public abstract void Work();

            /// <summary>The interpreted opcodes waiting on the compile, for the hot order.</summary>
            public abstract long Waiting();
        }

        private sealed class Job<TState>(TState state, Action<TState> work, Func<TState, long>? waiting) : Job
        {
            public override void Work() => work(state);

            public override long Waiting() => waiting is null ? 0 : waiting(state);
        }

        /// <summary>Guards the jobs and the worker count; the workers wait on it.</summary>
        private static readonly object _gate = new();
        /// <summary>The queued jobs, oldest first.</summary>
        private static readonly List<Job> _jobs = [];
        private static int _pending;
        private static int _workers;
        /// <summary>The time the workers have spent on jobs, summed over them, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
        public static long BusyTicks;

        public static int Pending => Volatile.Read(ref _pending);

        public static int Workers => Volatile.Read(ref _workers);

        /// <summary>Whether the compiler thread runs under Linux's SCHED_IDLE, for diagnostics: null until it starts.</summary>
        public static bool? IdleScheduling;

        private struct SchedParam
        {
            public int Priority;
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "sched_setscheduler", SetLastError = true)]
        private static extern int SchedSetScheduler(int pid, int policy, ref SchedParam param);

        /// <summary>Moves the calling thread to SCHED_IDLE, which an unprivileged thread may always do; false if the call fails.</summary>
        internal static bool SetIdleScheduling()
        {
            const int SchedIdle = 5;
            try
            {
                SchedParam param = default;
                // pid 0 is the calling thread.
                return SchedSetScheduler(0, SchedIdle, ref param) == 0;
            }
            catch (Exception exception)
            {
                IlEvmSettings.RecordCaught("idle scheduling", exception);
                return false;
            }
        }

        /// <summary>Queues <paramref name="work"/> for the background compiler, unless <see cref="IlEvmSettings.MaxPendingCompiles"/> wait already.</summary>
        public static bool Enqueue(Action work) => Enqueue(work, null);

        /// <summary>As <see cref="Enqueue(Action)"/>, with <paramref name="waiting"/> giving the interpreted opcodes waiting on the compile, for the hot order; read racily under the lock, null counting as none.</summary>
        public static bool Enqueue(Action work, Func<long>? waiting) =>
            Enqueue((Work: work, Waiting: waiting), static s => s.Work(), static s => s.Waiting?.Invoke() ?? 0);

        /// <summary>As <see cref="Enqueue(Action, Func{long})"/>, on <paramref name="state"/>: nothing is allocated unless the queue takes the job.</summary>
        public static bool Enqueue<TState>(TState state, Action<TState> work, Func<TState, long>? waiting)
        {
            lock (_gate)
            {
                if (_jobs.Count >= IlEvmSettings.MaxPendingCompiles)
                    return false;
                _jobs.Add(new Job<TState>(state, work, waiting));
                Volatile.Write(ref _pending, _jobs.Count);
                // Started here, under the lock, so that the count and the threads agree; raising the setting adds threads.
                int wanted = Math.Max(1, IlEvmSettings.CompileThreads);
                while (_workers < wanted)
                {
                    int index = _workers;
                    Volatile.Write(ref _workers, index + 1);
                    Thread worker = new(Run) { IsBackground = true, Name = index == 0 ? "EVM IL compiler" : $"EVM IL compiler {index + 1}", Priority = IlEvmSettings.CompilePriority };
                    worker.Start(index);
                }

                Monitor.Pulse(_gate);
            }

            return true;
        }

        /// <summary>The next job, under the lock: the oldest, or under the hot order the one most interpreted opcodes wait on, the oldest of equals.</summary>
        private static Job Take()
        {
            int index = 0;
            if (IlEvmSettings.CompileOrderHot)
            {
                long most = long.MinValue;
                for (int i = 0; i < _jobs.Count; i++)
                {
                    long waiting = _jobs[i].Waiting();
                    if (waiting > most)
                    {
                        most = waiting;
                        index = i;
                    }
                }
            }

            Job job = _jobs[index];
            _jobs.RemoveAt(index);
            Volatile.Write(ref _pending, _jobs.Count);
            return job;
        }

        private static void Run(object? state)
        {
            int index = (int)state!;
            if (IlEvmSettings.CompileIdle && OperatingSystem.IsLinux())
                IdleScheduling = SetIdleScheduling();
            while (true)
            {
                Job job;
                lock (_gate)
                {
                    while (true)
                    {
                        // A worker past a lowered count leaves once idle, the last started first, so the rest keep their numbers.
                        if (index >= Math.Max(1, IlEvmSettings.CompileThreads) && index == _workers - 1)
                        {
                            Volatile.Write(ref _workers, index);
                            // The wake-up it may have taken goes on to a worker that stays.
                            Monitor.Pulse(_gate);
                            return;
                        }

                        if (_jobs.Count > 0)
                            break;
                        Monitor.Wait(_gate, 1_000);
                    }

                    job = Take();
                }

                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    job.Work();
                }
                catch (Exception exception)
                {
                    // IlCompileInto records its own failures; anything else is recorded here and never takes the worker down.
                    IlEvmSettings.RecordCaught("background compiler", exception);
                }

                Interlocked.Add(ref BusyTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }
        }
    }
}
