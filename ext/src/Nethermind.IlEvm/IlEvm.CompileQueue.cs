// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.IlEvm;

public static unsafe partial class IlEvm
{
    /// <summary>The Legacy rules' background compiler of layouts and chunks, on <see cref="IlEvmSettings.CompileThreads"/> threads, in <see cref="IlEvmSettings.CompileOrderHot"/> order.</summary>
    /// <remarks>
    /// Compiles of code a request brought (<see cref="IsRequestCode"/>) never take the last quarter of the queue, which
    /// stays the chain's code's, and run only within a budget of compile time: a bucket of <see cref="RequestBurstMs"/>
    /// that fills by <see cref="RequestShare"/> of each second, charged with the compile thread's CPU time
    /// (<see cref="IlThreadTime"/>). Past it they wait, while the chain's code compiles as before.
    /// </remarks>
    internal static class IlCompileQueue
    {
        private abstract class Job
        {
            /// <summary>Whether the job compiles code a request brought, which the budget of compile time paces.</summary>
            public bool Request;

            public abstract void Work();

            /// <summary>The interpreted opcodes waiting on the compile, for the hot order.</summary>
            public abstract long Waiting();
        }

        private sealed class Job<TState>(TState state, Action<TState> work, Func<TState, long>? waiting) : Job
        {
            public override void Work() => work(state);

            public override long Waiting() => waiting is null ? 0 : waiting(state);
        }

        /// <summary>Guards the jobs, the worker count and the budget; the workers wait on it.</summary>
        private static readonly object _gate = new();
        /// <summary>The queued jobs, oldest first.</summary>
        private static readonly List<Job> _jobs = [];
        private static int _pending;
        private static int _requestJobs;
        private static int _workers;

        /// <summary>The share of each second that compiles of request code may take on average: half of one compile thread. Settable for tests.</summary>
        internal static double RequestShare = 0.5;

        /// <summary>The compile time that compiles of request code may take at once, which a node's warm-up mostly fits in. Settable for tests.</summary>
        internal static double RequestBurstMs = 10_000;

        /// <summary>The budget left, in milliseconds, as of <see cref="_requestBudgetAt"/>; NaN for a full bucket not used yet. Below zero after a compile that ran past it.</summary>
        private static double _requestBudgetMs = double.NaN;
        private static long _requestBudgetAt;

        /// <summary>The compiles of request code waiting.</summary>
        public static int PendingRequests => Volatile.Read(ref _requestJobs);

        /// <summary>The budget of compile time left for request code, in milliseconds, brought up to <paramref name="now"/>; under the lock.</summary>
        private static double RequestBudget(long now)
        {
            if (double.IsNaN(_requestBudgetMs))
                _requestBudgetMs = RequestBurstMs;
            else
                _requestBudgetMs = Math.Min(RequestBurstMs, _requestBudgetMs + (now - _requestBudgetAt) * 1_000.0 / System.Diagnostics.Stopwatch.Frequency * RequestShare);
            _requestBudgetAt = now;
            return _requestBudgetMs;
        }

        /// <summary>Starts the budget of compile time for request code full again, and wakes the workers to it, for tests.</summary>
        internal static void ResetRequestBudget()
        {
            lock (_gate)
            {
                _requestBudgetMs = double.NaN;
                Monitor.PulseAll(_gate);
            }
        }
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
        /// <param name="request">Whether the work compiles code a request brought, which the queue holds and runs within its share and budget.</param>
        public static bool Enqueue(Action work, Func<long>? waiting, bool request = false) =>
            Enqueue((Work: work, Waiting: waiting), static s => s.Work(), static s => s.Waiting?.Invoke() ?? 0, request);

        /// <summary>As <see cref="Enqueue(Action, Func{long}, bool)"/>, on <paramref name="state"/>: nothing is allocated unless the queue takes the job.</summary>
        public static bool Enqueue<TState>(TState state, Action<TState> work, Func<TState, long>? waiting, bool request = false)
        {
            lock (_gate)
            {
                // The last quarter of the queue stays the chain's code's: a warm-up of many requests' code fills the rest.
                int maxPending = IlEvmSettings.MaxPendingCompiles;
                if (_jobs.Count >= maxPending || (request && _requestJobs >= maxPending - Math.Max(1, maxPending / 4)))
                    return false;
                _jobs.Add(new Job<TState>(state, work, waiting) { Request = request });
                if (request)
                    Volatile.Write(ref _requestJobs, _requestJobs + 1);
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

        /// <summary>
        /// The next job, under the lock: the oldest, or under the hot order the one most interpreted opcodes wait on, the
        /// oldest of equals; compiles of request code only while their budget lasts. Null when none of the jobs may run.
        /// </summary>
        private static Job? Take(long now)
        {
            bool requests = RequestBudget(now) > 0;
            int index = -1;
            long most = long.MinValue;
            for (int i = 0; i < _jobs.Count; i++)
            {
                if (_jobs[i].Request && !requests)
                    continue;
                if (!IlEvmSettings.CompileOrderHot)
                {
                    index = i;
                    break;
                }

                long waiting = _jobs[i].Waiting();
                if (waiting > most)
                {
                    most = waiting;
                    index = i;
                }
            }

            if (index < 0)
                return null;
            Job job = _jobs[index];
            _jobs.RemoveAt(index);
            if (job.Request)
                Volatile.Write(ref _requestJobs, _requestJobs - 1);
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
                Job? job = null;
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

                        long now = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (_jobs.Count > 0)
                        {
                            job = Take(now);
                            if (job is not null)
                                break;
                        }

                        // Only compiles of request code wait, their budget spent: until it is back above zero, or a job comes.
                        int wait = _jobs.Count == 0 || RequestShare <= 0 ? 1_000 : (int)Math.Clamp(-RequestBudget(now) / RequestShare + 1, 1, 1_000);
                        Monitor.Wait(_gate, wait);
                    }
                }

                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                double cpuStarted = job!.Request ? IlThreadTime.Ms() : double.NaN;
                try
                {
                    job.Work();
                }
                catch (Exception exception)
                {
                    // IlCompileInto records its own failures; anything else is recorded here and never takes the worker down.
                    IlEvmSettings.RecordCaught("background compiler", exception);
                }

                long finished = System.Diagnostics.Stopwatch.GetTimestamp();
                Interlocked.Add(ref BusyTicks, finished - started);
                if (job.Request)
                {
                    // The CPU time the job took: a busy machine's wait for a core is no compile, and must not hold the next back.
                    double cpu = IlThreadTime.Ms();
                    double spent = double.IsNaN(cpu) || double.IsNaN(cpuStarted) ? (finished - started) * 1_000.0 / System.Diagnostics.Stopwatch.Frequency : cpu - cpuStarted;
                    lock (_gate)
                        _requestBudgetMs = RequestBudget(finished) - spent;
                }
            }
        }
    }
}
