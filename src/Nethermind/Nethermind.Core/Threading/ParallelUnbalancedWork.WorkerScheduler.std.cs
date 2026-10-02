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
        private int _requested;
        private int _runners;

        internal WorkerScheduler(int concurrency, Action<IThreadPoolWorkItem>? schedule = null,
            WorkerGroup? group = null, WorkerScheduler? parent = null)
        {
            Concurrency = concurrency;
            Parent = parent;
            Group = group ?? parent?.Group;
            _schedule = parent is null ? schedule ?? QueueToThreadPool : parent.ScheduleChildRunner;
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
            internal int Count;
            internal int ReadyDescendants;
            internal WorkQueue? Previous;
            internal WorkQueue? Next;
        }

        internal void Enqueue(WorkQueue queue, IThreadPoolWorkItem work, bool resuming = false, int count = 1)
        {
            if (count <= 0) return;
            int schedule;
            lock (_gate)
            {
                Debug.Assert(queue.Count == 0 || ReferenceEquals(queue.Work, work), "A work queue holds a single work item.");
                if (queue.Count == 0)
                {
                    queue.Work = work;
                    AddReady(queue);
                    UpdateReadyAncestors(queue, 1);
                }
                queue.Count += count;
                _pending += count;
                // A yielding runner can drain its own next batch. Already requested runners also
                // cover pending work, even before the thread pool starts their callbacks.
                schedule = Math.Min(Concurrency - 1 - _runners,
                    _pending - _requested - (resuming && ReferenceEquals(_context.Runner, this) ? 1 : 0));
                if (schedule > 0)
                {
                    _runners += schedule;
                    _requested += schedule;
                }
            }
            for (WorkQueue? parent = queue.Parent; parent is not null; parent = parent.Parent)
                parent.Owner?.NotifyWorkAvailable();
            for (int i = 0; i < schedule; i++) _schedule(this);
        }

        internal bool TryExecute(WorkQueue queue, bool includeDescendants = false)
        {
            IThreadPoolWorkItem work;
            lock (_gate)
            {
                if (queue.Count == 0)
                {
                    if (!includeDescendants || FindDescendant(queue) is not { } descendant) return false;
                    queue = descendant;
                }
                work = Take(queue);
            }
            Run(work, queue);
            return true;
        }

        internal bool HasReadyWork(WorkQueue queue)
        {
            lock (_gate) return queue.Count != 0 || queue.ReadyDescendants != 0;
        }

        // Ancestry stops at background operations; only nested synchronous loops are eligible.
        private WorkQueue? FindDescendant(WorkQueue ancestor)
        {
            if (ancestor.ReadyDescendants == 0) return null;
            for (WorkQueue? queue = _first; queue is not null; queue = queue.Next)
                for (WorkQueue? parent = queue.Parent; parent is not null; parent = parent.Parent)
                    if (ReferenceEquals(parent, ancestor)) return queue;
            return null;
        }

        /// <summary>Removes the queue's unstarted callbacks without running them.</summary>
        /// <returns>The number of callbacks removed.</returns>
        internal int Withdraw(WorkQueue queue)
        {
            lock (_gate)
            {
                int count = queue.Count;
                if (count == 0) return 0;
                queue.Count = 0;
                queue.Work = null;
                UpdateReadyAncestors(queue, -1);
                _pending -= count;
                Unlink(queue);
                return count;
            }
        }

        private static void UpdateReadyAncestors(WorkQueue queue, int delta)
        {
            for (WorkQueue? parent = queue.Parent; parent is not null; parent = parent.Parent)
                parent.ReadyDescendants += delta;
        }

        private void AddReady(WorkQueue queue)
        {
            queue.Previous = _last;
            queue.Next = null;
            if (_last is null) _first = queue;
            else _last.Next = queue;
            _last = queue;
        }

        private IThreadPoolWorkItem Take(WorkQueue queue)
        {
            IThreadPoolWorkItem work = queue.Work!;
            if (--queue.Count == 0)
            {
                queue.Work = null;
                UpdateReadyAncestors(queue, -1);
            }
            _pending--;
            Unlink(queue);
            // Rotate ready operations without moving their callbacks. Joining a specific operation uses
            // this same constant-time removal and never executes unrelated callbacks.
            if (queue.Count > 0) AddReady(queue);
            return work;
        }

        private void Unlink(WorkQueue queue)
        {
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
                lock (_gate) _requested--;
                while (true)
                {
                    IThreadPoolWorkItem work;
                    WorkQueue queue;
                    lock (_gate)
                    {
                        // Enqueue and retirement share the gate: a producer either sees a live
                        // drainer or reserves a replacement, so work cannot lose its wake-up.
                        if (_first is null)
                        {
                            _runners--;
                            return;
                        }
                        queue = _first;
                        work = Take(queue);
                    }
                    Run(work, queue);
                }
            }
            finally { context = previous; }
        }
    }
}
