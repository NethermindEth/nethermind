// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;

namespace Nethermind.Core.Threading;

public partial class ParallelUnbalancedWork
{
    internal static partial WorkerGroup? GetCurrentGroup() => WorkerScope.Current?.Group;

    internal sealed partial class WorkerGroup
    {
        internal WorkerScope Root { get; private set; } = null!;
        private partial void Initialize() => Root = new(this, detached: true);
        internal partial WorkerScope Enter() => new(this, detached: false);
        internal partial void Queue(IThreadPoolWorkItem work)
        {
            using WorkerScope scope = Enter();
            if (Concurrency == 1) work.Execute();
            else Root.Enqueue(new(), work);
        }
    }

    public sealed partial class WorkerScope : IThreadPoolWorkItem
    {
        [ThreadStatic] private static WorkerContext _context;

        private struct WorkerContext
        {
            internal WorkerScope? Scope;
            internal WorkerScope? Runner;
            internal WorkerScope? GroupRunner;
            internal WorkQueue? Operation;
        }

        internal static WorkerScope? Current => _context.Scope;
        /// <summary>Whether operations other than <paramref name="own"/> have queued callbacks; a lock-free hint.</summary>
        internal bool HasOtherReadyWork(WorkQueue own) =>
            Volatile.Read(ref _root._first) is { } first && (!ReferenceEquals(first, own) || Volatile.Read(ref first.Next) is not null);
        private readonly WorkerContext _previous;
        private readonly WorkerScope _root;
        private readonly object _gate;
        private readonly Action<IThreadPoolWorkItem> _schedule;
        private readonly object _callerGate;
        private readonly bool _ownsCaller;
        private readonly bool _limited;
        private readonly WorkerScope? _parentEntry;
        internal WorkerGroup? Group { get; }
        private WorkQueue? _first;
        private WorkQueue? _last;
        private int _pending;
        private int _requested;
        private int _runners;
        private bool _disposed;
        internal int Concurrency { get; }

        internal WorkerScope(int concurrency, Action<IThreadPoolWorkItem>? schedule = null, bool limitConcurrency = false)
        {
            ref WorkerContext context = ref _context;
            _previous = context;
            if (limitConcurrency && context.Scope is { } parent && concurrency < parent.Concurrency)
            {
                _root = this;
                _gate = new();
                _callerGate = new();
                Concurrency = concurrency;
                Group = parent.Group;
                _schedule = parent._root.ScheduleChildRunner;
                _limited = true;
                _ownsCaller = true;
                Monitor.Enter(_callerGate);
                context.Scope = this;
                context.Operation = null;
                return;
            }
            _root = context.Scope?._root ?? this;
            _gate = context.Scope?._gate ?? new();
            _callerGate = _root == this ? new() : _root._callerGate;
            Concurrency = context.Scope?.Concurrency ?? concurrency;
            Group = _root == this ? null : _root.Group;
            _schedule = schedule ?? QueueToThreadPool;
            context.Scope = this;
        }

        internal WorkerScope(WorkerGroup group, bool detached)
        {
            _previous = detached ? default : _context;
            _root = detached ? this : group.Root;
            _gate = _root == this ? new() : _root._gate;
            _callerGate = _root == this ? new() : _root._callerGate;
            _schedule = QueueToThreadPool;
            Concurrency = group.Concurrency;
            Group = group;
            if (detached) return;

            bool groupRunner = ReferenceEquals(_previous.GroupRunner, _root);
            _ownsCaller = !ReferenceEquals(_previous.Scope?._root, _root) && !ReferenceEquals(_previous.Runner, _root) && !groupRunner;
            if (_ownsCaller) Monitor.Enter(_root._callerGate);
            _context = new()
            {
                Scope = this,
                Runner = ReferenceEquals(_previous.Runner, _root) || groupRunner ? _root : null,
                GroupRunner = groupRunner ? _root : null,
                Operation = ReferenceEquals(_previous.Scope?._root, _root) ? _previous.Operation : null
            };
        }

        private WorkerScope(WorkerScope root)
        {
            _parentEntry = root.Group is { } group && !ReferenceEquals(group, GetCurrentGroup()) ? group.Enter() : null;
            _previous = _context;
            _root = root;
            _gate = root._gate;
            _callerGate = root._callerGate;
            _schedule = root._schedule;
            Concurrency = root.Concurrency;
            Group = root.Group;
            _ownsCaller = !ReferenceEquals(_previous.Scope?._root, root) && !ReferenceEquals(_previous.Runner, root);
            if (_ownsCaller) Monitor.Enter(_callerGate);
            _context.Scope = this;
            if (!ReferenceEquals(_previous.Scope?._root, root)) _context.Operation = null;
        }

        internal WorkerScope? EnterForJoin()
        {
            if (_root._limited && !ReferenceEquals(_context.Scope?._root, _root) && !ReferenceEquals(_context.Runner, _root))
                return new(_root);
            return Group is { } group && !ReferenceEquals(group, GetCurrentGroup()) ? group.Enter() : null;
        }

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
            WorkerScope root = _root;
            int schedule;
            lock (root._gate)
            {
                Debug.Assert(queue.Count == 0 || ReferenceEquals(queue.Work, work), "A work queue holds a single work item.");
                if (queue.Count == 0)
                {
                    queue.Work = work;
                    root.AddReady(queue);
                    UpdateReadyAncestors(queue, 1);
                }
                queue.Count += count;
                root._pending += count;
                // A yielding runner can drain its own next batch. Already requested runners also
                // cover pending work, even before the thread pool starts their callbacks.
                schedule = Math.Min(Concurrency - 1 - root._runners,
                    root._pending - root._requested - (resuming && ReferenceEquals(_context.Runner, root) ? 1 : 0));
                if (schedule > 0)
                {
                    root._runners += schedule;
                    root._requested += schedule;
                }
            }
            for (WorkQueue? parent = queue.Parent; parent is not null; parent = parent.Parent)
                parent.Owner?.NotifyWorkAvailable();
            for (int i = 0; i < schedule; i++) root._schedule(root);
        }

        internal bool TryExecute(WorkQueue queue, bool includeDescendants = false)
        {
            IThreadPoolWorkItem work;
            lock (_root._gate)
            {
                if (queue.Count == 0)
                {
                    if (!includeDescendants || FindDescendant(queue) is not { } descendant) return false;
                    queue = descendant;
                }
                work = _root.Take(queue);
            }
            Run(work, queue);
            return true;
        }

        internal bool HasReadyWork(WorkQueue queue)
        {
            lock (_root._gate) return queue.Count != 0 || queue.ReadyDescendants != 0;
        }

        // Ancestry stops at background operations; only nested synchronous loops are eligible.
        private WorkQueue? FindDescendant(WorkQueue ancestor)
        {
            if (ancestor.ReadyDescendants == 0) return null;
            for (WorkQueue? queue = _root._first; queue is not null; queue = queue.Next)
                for (WorkQueue? parent = queue.Parent; parent is not null; parent = parent.Parent)
                    if (ReferenceEquals(parent, ancestor)) return queue;
            return null;
        }

        /// <summary>Removes the queue's unstarted callbacks without running them.</summary>
        /// <returns>The number of callbacks removed.</returns>
        internal int Withdraw(WorkQueue queue)
        {
            lock (_root._gate)
            {
                int count = queue.Count;
                if (count == 0) return 0;
                queue.Count = 0;
                queue.Work = null;
                UpdateReadyAncestors(queue, -1);
                _root._pending -= count;
                _root.Unlink(queue);
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

        private static void QueueToThreadPool(IThreadPoolWorkItem work)
            => ThreadPool.UnsafeQueueUserWorkItem(work, preferLocal: false);

        internal void Run(IThreadPoolWorkItem work, WorkQueue operation)
        {
            ref WorkerContext context = ref _context;
            WorkerScope? previous = context.Scope;
            WorkQueue? previousOperation = context.Operation;
            context.Operation = operation;
            if (!ReferenceEquals(previous, _root)) context.Scope = _root;
            try { work.Execute(); }
            finally
            {
                context.Operation = previousOperation;
                if (!ReferenceEquals(context.Scope, previous)) context.Scope = previous;
            }
        }

        void IThreadPoolWorkItem.Execute()
        {
            ref WorkerContext context = ref _context;
            WorkerContext previous = context;
            context = new()
            {
                Scope = this,
                Runner = this,
                GroupRunner = ReferenceEquals(this, Group?.Root) ? this : previous.GroupRunner
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

        public partial void Dispose()
        {
            if (_disposed) return;
            Debug.Assert(ReferenceEquals(_context.Scope, this), "Worker scopes must be disposed on their owning thread in reverse order.");
            _disposed = true;
            _context = _previous;
            if (_ownsCaller) Monitor.Exit(_root._callerGate);
            _parentEntry?.Dispose();
        }
    }

    private static void QueueWorkers(BaseData data, IThreadPoolWorkItem work, int count)
    {
        if (count == 0) return;
        if (WorkerScope.Current is { } scope)
        {
            data.Scope = scope;
            scope.Enqueue(data.Queue = new(), work, count: count);
        }
        else
        {
            data.SetUnstarted(count);
            for (int i = 0; i < count; i++)
                ThreadPool.UnsafeQueueUserWorkItem(work, preferLocal: false);
        }
    }

    private static void WaitForWorkers(BaseData data)
    {
        // The caller has claimed the range, so unstarted workers would only retire; withdraw them in one step
        // rather than running each. Unrelated callbacks stay queued: they may depend on the caller's progress.
        // Without a scope the callbacks stay in the thread pool, but they are withdrawn all the same: waiting
        // for a free thread to dequeue them blocks this thread on the pool, and nested loops (a parallel
        // BulkSet recursing into another) then park every pool thread on workers that cannot start.
        int withdrawn = data.Scope is { } scope ? scope.Withdraw(data.Queue!) : data.WithdrawUnstarted();
        if (withdrawn > 0) data.MarkThreadCompleted(withdrawn);
        if (data.ActiveThreads > 0) data.Event.Wait();
    }
}
