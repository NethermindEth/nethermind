// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;

namespace Nethermind.Core.Threading;

public partial class ParallelUnbalancedWork
{
    internal sealed class WorkerScheduler : IThreadPoolWorkItem
    {
        private const long CounterUnit = 1L << 32;

        internal static WorkerScheduler? Current => _context.Scheduler;
        private readonly object _gate = new();
        internal readonly object CallerGate = new();
        private readonly Action<IThreadPoolWorkItem> _schedule;
        internal WorkerGroup? Group { get; }
        internal WorkerScheduler? Parent { get; }
        internal int Concurrency { get; }
        internal bool IsGroup => Group is not null && Parent is null;
        private WorkQueue? _first;
        private WorkQueue? _last;
        private int _pending;
        // Low bits count reserved runners; high bits count those that have not started.
        private long _runners;

        internal WorkerScheduler(int concurrency, Action<IThreadPoolWorkItem>? schedule = null,
            WorkerGroup? group = null, WorkerScheduler? parent = null)
        {
            Concurrency = concurrency;
            Parent = parent;
            Group = group ?? parent?.Group;
            _schedule = parent is null ? schedule ?? QueueToThreadPool : parent.ScheduleChildRunner;
        }

        private static long s_lingerUntil;

        internal static long LingerUntil
        {
            get => Volatile.Read(ref s_lingerUntil);
            set => Volatile.Write(ref s_lingerUntil, value);
        }

        /// <summary>Spins while the linger window is open and nothing is ready; true when work arrived.</summary>
        private bool Linger()
        {
            long until = LingerUntil;
            if (until == 0 || Stopwatch.GetTimestamp() >= until) return false;
            SpinWait spinner = default;
            while (Volatile.Read(ref _first) is null)
            {
                if (Stopwatch.GetTimestamp() >= until || LingerUntil == 0) return false;
                spinner.SpinOnce(sleep1Threshold: -1);
            }

            return true;
        }

        internal (int Reserved, int Unstarted, int Pending) Load()
        {
            long runners = Volatile.Read(ref _runners);
            return ((int)runners, (int)(runners >> 32), Volatile.Read(ref _pending));
        }

        internal WorkerScope? EnterForJoin()
        {
            WorkerScheduler? current = Current;
            if (ReferenceEquals(current, this)) return null;
            if (Parent is not null && !ReferenceEquals(_context.Runner, this))
                return new(this);
            return Group is { } group && !ReferenceEquals(group, current?.Group) ? group.Enter() : null;
        }

        /// <summary>Whether operations other than <paramref name="own"/> have queued callbacks; a lock-free hint.</summary>
        internal bool HasOtherReadyWork(WorkQueue own) =>
            Volatile.Read(ref _first) is { } first && (!ReferenceEquals(first, own) || Volatile.Read(ref first.Next) is not null);

        private static void QueueToThreadPool(IThreadPoolWorkItem work)
            => ThreadPool.UnsafeQueueUserWorkItem(work, preferLocal: false);

        private void ScheduleChildRunner(IThreadPoolWorkItem work) => Enqueue(new(detached: true), work);

        /// <summary>One operation's unstarted callbacks, all the same work item.</summary>
        internal sealed class WorkQueue(BackgroundWork? owner = null, bool detached = false)
        {
            // Synchronous loops record the operation that started them.
            // Background operations start a new ancestry: their creator need not join them.
            internal readonly WorkQueue? Parent = owner is null && !detached ? _context.Operation : null;
            internal readonly BackgroundWork? Owner = owner;
            // Cleared when the last callback is taken or withdrawn.
            internal IThreadPoolWorkItem? Work;
            // The stamp changes on reuse so a delayed claimant cannot take a newer batch's work item.
            internal long State;
            internal int Count => (int)Volatile.Read(ref State);
            internal int ReadyDescendants;
            internal bool Linked;
            internal WorkQueue? Previous;
            internal WorkQueue? Next;
        }

        internal void Enqueue(WorkQueue queue, IThreadPoolWorkItem work, bool resuming = false, int count = 1)
        {
            if (count <= 0) return;
            while (true)
            {
                long state = Volatile.Read(ref queue.State);
                while ((int)state > 0)
                {
                    IThreadPoolWorkItem? queuedWork = Volatile.Read(ref queue.Work);
                    Interlocked.Add(ref _pending, count);
                    long observed = Interlocked.CompareExchange(ref queue.State, state + count, state);
                    if (observed == state)
                    {
                        Debug.Assert(ReferenceEquals(queuedWork, work), "A work queue holds a single work item.");
                        NotifyAndRequestRunners(queue, resuming);
                        return;
                    }
                    Interlocked.Add(ref _pending, -count);
                    state = observed;
                }
                lock (_gate)
                {
                    state = Volatile.Read(ref queue.State);
                    if ((int)state != 0) continue;
                    queue.Work = work;
                    if (!queue.Linked)
                    {
                        AddReady(queue);
                        UpdateReadyAncestors(queue, 1);
                    }
                    Interlocked.Add(ref _pending, count);
                    Volatile.Write(ref queue.State, ((state & ~(long)uint.MaxValue) + CounterUnit) | (uint)count);
                }
                break;
            }
            NotifyAndRequestRunners(queue, resuming);
        }

        private void NotifyAndRequestRunners(WorkQueue queue, bool resuming)
        {
            for (WorkQueue? parent = queue.Parent; parent is not null; parent = parent.Parent)
                parent.Owner?.NotifyWorkAvailable();
            RequestRunners(resuming);
        }

        private void RequestRunners(bool resuming = false)
        {
            int covered = resuming && ReferenceEquals(_context.Runner, this) ? 1 : 0;
            long runners = Volatile.Read(ref _runners);
            while (true)
            {
                int count = Math.Min(Concurrency - 1 - (int)runners,
                    Volatile.Read(ref _pending) - (int)(runners >> 32) - covered);
                if (count <= 0) return;
                long observed = Interlocked.CompareExchange(ref _runners, runners + (CounterUnit + 1) * count, runners);
                if (observed == runners)
                {
                    for (int i = 0; i < count; i++) _schedule(this);
                    return;
                }
                runners = observed;
            }
        }

        internal bool TryExecute(WorkQueue queue, bool includeDescendants = false)
        {
            IThreadPoolWorkItem? work = TryTake(queue);
            if (work is null)
            {
                if (!includeDescendants || Volatile.Read(ref queue.ReadyDescendants) == 0) return false;
                if (FindDescendant(queue) is not { } descendant) return false;
                queue = descendant;
                work = TryTake(queue);
                if (work is null) return false;
            }
            Run(work, queue);
            return true;
        }

        internal bool HasReadyWork(WorkQueue queue)
            => queue.Count != 0 || Volatile.Read(ref queue.ReadyDescendants) != 0;

        // Ancestry stops at background operations; only nested synchronous loops are eligible.
        private WorkQueue? FindDescendant(WorkQueue ancestor)
        {
            lock (_gate)
            {
                for (WorkQueue? queue = _first; queue is not null; queue = queue.Next)
                    if (queue.Count != 0)
                        for (WorkQueue? parent = queue.Parent; parent is not null; parent = parent.Parent)
                            if (ReferenceEquals(parent, ancestor)) return queue;
                return null;
            }
        }

        /// <summary>Removes the queue's unstarted callbacks without running them.</summary>
        /// <returns>The number of callbacks removed.</returns>
        internal int Withdraw(WorkQueue queue)
        {
            long state = Volatile.Read(ref queue.State);
            while ((int)state > 0)
            {
                long drained = state & ~(long)uint.MaxValue;
                long observed = Interlocked.CompareExchange(ref queue.State, drained, state);
                if (observed == state)
                {
                    int count = (int)state;
                    Interlocked.Add(ref _pending, -count);
                    RemoveIfDrained(queue, drained);
                    return count;
                }
                state = observed;
            }
            return 0;
        }

        private static void UpdateReadyAncestors(WorkQueue queue, int delta)
        {
            for (WorkQueue? parent = queue.Parent; parent is not null; parent = parent.Parent)
                Interlocked.Add(ref parent.ReadyDescendants, delta);
        }

        private void AddReady(WorkQueue queue)
        {
            queue.Linked = true;
            queue.Previous = _last;
            queue.Next = null;
            if (_last is null) _first = queue;
            else _last.Next = queue;
            _last = queue;
        }

        private IThreadPoolWorkItem? TryTake(WorkQueue queue)
        {
            long state = Volatile.Read(ref queue.State);
            while ((int)state > 0)
            {
                IThreadPoolWorkItem? work = Volatile.Read(ref queue.Work);
                long observed = Interlocked.CompareExchange(ref queue.State, state - 1, state);
                if (observed == state)
                {
                    Interlocked.Decrement(ref _pending);
                    if ((int)state == 1) RemoveIfDrained(queue, state - 1);
                    return work;
                }
                state = observed;
            }
            return null;
        }

        private void RemoveIfDrained(WorkQueue queue, long drained)
        {
            if ((int)drained != 0) return;
            lock (_gate)
            {
                if (Volatile.Read(ref queue.State) != drained) return;
                queue.Work = null;
                if (!queue.Linked) return;
                UpdateReadyAncestors(queue, -1);
                Unlink(queue);
            }
        }

        private void RotateReady(WorkQueue queue)
        {
            if (!ReferenceEquals(Volatile.Read(ref _first), queue) || Volatile.Read(ref queue.Next) is null) return;
            lock (_gate)
            {
                if (ReferenceEquals(_first, queue) && queue.Next is not null && queue.Count != 0)
                {
                    Unlink(queue);
                    AddReady(queue);
                }
            }
        }

        private void Unlink(WorkQueue queue)
        {
            queue.Linked = false;
            if (queue.Previous is null) _first = queue.Next;
            else queue.Previous.Next = queue.Next;
            if (queue.Next is null) _last = queue.Previous;
            else queue.Next.Previous = queue.Previous;
            queue.Previous = queue.Next = null;
        }

        internal void Run(IThreadPoolWorkItem work, WorkQueue operation)
        {
            ref WorkerContext context = ref _context;
            WorkerScheduler? previous = context.Scheduler;
            WorkQueue? previousOperation = context.Operation;
            context.Operation = operation;
            context.Scheduler = this;
            try { work.Execute(); }
            finally
            {
                context.Operation = previousOperation;
                context.Scheduler = previous;
            }
        }

        void IThreadPoolWorkItem.Execute()
        {
            ref WorkerContext context = ref _context;
            WorkerContext previous = context;
            context = new()
            {
                Scheduler = this,
                Runner = this,
                GroupRunner = IsGroup ? this : previous.GroupRunner
            };
            try
            {
                Interlocked.Add(ref _runners, -CounterUnit);
                while (true)
                {
                    WorkQueue? queue = Volatile.Read(ref _first);
                    if (queue is null && Linger()) continue;
                    if (queue is null)
                    {
                        // Release the reservation before rechecking publication, so either this
                        // runner or the producer requests a replacement for newly ready work.
                        Interlocked.Decrement(ref _runners);
                        if (Volatile.Read(ref _first) is not null) RequestRunners();
                        return;
                    }
                    if (TryTake(queue) is { } work)
                    {
                        if (queue.Count != 0) RotateReady(queue);
                        Run(work, queue);
                    }
                    else RemoveIfDrained(queue, Volatile.Read(ref queue.State));
                }
            }
            finally { context = previous; }
        }
    }
}
