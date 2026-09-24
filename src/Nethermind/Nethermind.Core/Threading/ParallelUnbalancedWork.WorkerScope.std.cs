// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Nethermind.Core.Threading;

public partial class ParallelUnbalancedWork
{
    public sealed partial class WorkerScope : IThreadPoolWorkItem
    {
        [ThreadStatic] private static WorkerContext _context;

        private struct WorkerContext
        {
            internal WorkerScope? Scope;
            internal WorkerScope? Runner;
        }

        internal static WorkerScope? Current => _context.Scope;
        /// <summary>Whether operations other than <paramref name="own"/> have queued callbacks; a lock-free hint.</summary>
        internal bool HasOtherReadyWork(WorkQueue own) =>
            Volatile.Read(ref _root._first) is { } first && (!ReferenceEquals(first, own) || Volatile.Read(ref first.Next) is not null);
        private readonly WorkerScope? _previous;
        private readonly WorkerScope _root;
        private readonly object _gate = new();
        private readonly Action<IThreadPoolWorkItem> _schedule;
        private WorkQueue? _first;
        private WorkQueue? _last;
        private int _pending;
        private int _requested;
        private int _runners;
        private bool _disposed;
        internal int Concurrency { get; }

        internal WorkerScope(int concurrency, Action<IThreadPoolWorkItem>? schedule = null)
        {
            ref WorkerContext context = ref _context;
            _previous = context.Scope;
            _root = _previous?._root ?? this;
            Concurrency = _previous?.Concurrency ?? concurrency;
            _schedule = schedule ?? QueueToThreadPool;
            context.Scope = this;
        }

        internal sealed class WorkQueue
        {
            internal readonly Queue<IThreadPoolWorkItem> Items = new();
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
                if (queue.Items.Count == 0) root.AddReady(queue);
                for (int i = 0; i < count; i++) queue.Items.Enqueue(work);
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
            for (int i = 0; i < schedule; i++) root._schedule(root);
        }

        internal bool TryExecute(WorkQueue queue)
        {
            IThreadPoolWorkItem work;
            lock (_root._gate)
            {
                if (queue.Items.Count == 0) return false;
                work = _root.Take(queue);
            }
            Run(work);
            return true;
        }

        /// <summary>Removes the queue's unstarted callbacks without running them.</summary>
        /// <returns>The number of callbacks removed.</returns>
        internal int Withdraw(WorkQueue queue)
        {
            lock (_root._gate)
            {
                int count = queue.Items.Count;
                if (count == 0) return 0;
                queue.Items.Clear();
                _root._pending -= count;
                _root.Unlink(queue);
                return count;
            }
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
            IThreadPoolWorkItem work = queue.Items.Dequeue();
            _pending--;
            Unlink(queue);
            // Rotate ready operations without moving their individual callbacks. Joining a specific
            // operation uses this same constant-time removal and never executes unrelated callbacks.
            if (queue.Items.Count > 0) AddReady(queue);
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

        internal void Run(IThreadPoolWorkItem work)
        {
            ref WorkerContext context = ref _context;
            WorkerScope? previous = context.Scope;
            if (!ReferenceEquals(previous, _root)) context.Scope = _root;
            try { work.Execute(); }
            finally
            {
                if (!ReferenceEquals(context.Scope, previous)) context.Scope = previous;
            }
        }

        void IThreadPoolWorkItem.Execute()
        {
            ref WorkerContext context = ref _context;
            WorkerContext previous = context;
            context = new() { Scope = this, Runner = this };
            try
            {
                lock (_gate) _requested--;
                while (true)
                {
                    IThreadPoolWorkItem work;
                    lock (_gate)
                    {
                        // Enqueue and retirement share the gate: a producer either sees a live
                        // drainer or reserves a replacement, so work cannot lose its wake-up.
                        if (_first is null)
                        {
                            _runners--;
                            return;
                        }
                        work = Take(_first);
                    }
                    Run(work);
                }
            }
            finally { context = previous; }
        }

        public partial void Dispose()
        {
            if (_disposed) return;
            Debug.Assert(ReferenceEquals(_context.Scope, this), "Worker scopes must be disposed on their owning thread in reverse order.");
            _disposed = true;
            _context.Scope = _previous;
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
            for (int i = 0; i < count; i++)
                ThreadPool.UnsafeQueueUserWorkItem(work, preferLocal: false);
        }
    }

    private static void WaitForWorkers(BaseData data)
    {
        // The caller has claimed the range, so unstarted workers would only retire; withdraw them in one step
        // rather than running each. Unrelated callbacks stay queued: they may depend on the caller's progress.
        if (data.Scope?.Withdraw(data.Queue!) is > 0 and var withdrawn)
            data.MarkThreadCompleted(withdrawn);
        if (data.ActiveThreads > 0) data.Event.Wait();
    }
}
