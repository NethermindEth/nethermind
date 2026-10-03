// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;

namespace Nethermind.Core.Threading;

public partial class ParallelUnbalancedWork
{
    [ThreadStatic] private static WorkerContext _context;

    private struct WorkerContext
    {
        internal WorkerScope? Scope;
        internal WorkerScheduler? Scheduler;
        internal WorkerScheduler? Runner;
        internal WorkerScheduler? GroupRunner;
        internal WorkerScheduler.WorkQueue? Operation;
    }

    internal static partial WorkerGroup? GetCurrentGroup() => WorkerScheduler.Current?.Group;

    public static partial void LingerRunnersUntil(long untilTimestamp) => WorkerScheduler.LingerUntil = untilTimestamp;

    public static partial WorkerScope BeginDetachedWorkerScope(int maxDegreeOfParallelism)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);
        return new(new WorkerScheduler(maxDegreeOfParallelism));
    }

    internal static partial (int Reserved, int Unstarted, int Pending) CurrentLoad() =>
        WorkerScheduler.Current is { } scheduler ? scheduler.Load() : (0, 0, 0);

    internal sealed partial class WorkerGroup
    {
        private WorkerScheduler _scheduler = null!;
        private partial void Initialize() => _scheduler = new(Concurrency, group: this);
        internal partial WorkerScope Enter() => new(_scheduler);
        internal partial void Queue(IThreadPoolWorkItem work)
        {
            using WorkerScope scope = Enter();
            if (Concurrency == 1) work.Execute();
            else _scheduler.Enqueue(new(), work);
        }
    }

    public sealed partial class WorkerScope
    {
        internal static WorkerScope? Current => _context.Scope;
        private readonly WorkerContext _previous;
        private readonly WorkerScope? _parentEntry;
        private readonly bool _ownsCaller;
        private bool _disposed;
        internal WorkerScheduler Scheduler { get; }

        internal WorkerScope(int concurrency, Action<IThreadPoolWorkItem>? schedule = null, bool limitConcurrency = false)
            : this(WorkerScheduler.Current is { } parent
                ? limitConcurrency && concurrency < parent.Concurrency ? new(concurrency, parent: parent) : parent
                : new(concurrency, schedule))
        {
        }

        internal WorkerScope(WorkerScheduler scheduler)
        {
            _parentEntry = scheduler.Parent is not null && scheduler.Group is { } group && !ReferenceEquals(group, GetCurrentGroup())
                ? group.Enter() : null;
            _previous = _context;
            Scheduler = scheduler;
            _ownsCaller = (scheduler.Group is not null || scheduler.Parent is not null)
                && !ReferenceEquals(_previous.Scheduler, scheduler)
                && !ReferenceEquals(_previous.Runner, scheduler)
                && !ReferenceEquals(_previous.GroupRunner, scheduler);
            if (_ownsCaller) Monitor.Enter(scheduler.CallerGate);
            _context.Scope = this;
            _context.Scheduler = scheduler;
            if (!ReferenceEquals(_previous.Scheduler, scheduler)) _context.Operation = null;
            if (scheduler.IsGroup)
            {
                bool groupRunner = ReferenceEquals(_previous.GroupRunner, scheduler);
                _context.Runner = ReferenceEquals(_previous.Runner, scheduler) || groupRunner ? scheduler : null;
                _context.GroupRunner = groupRunner ? scheduler : null;
            }
        }

        public partial void Dispose()
        {
            if (_disposed) return;
            Debug.Assert(ReferenceEquals(_context.Scope, this), "Worker scopes must be disposed on their owning thread in reverse order.");
            _disposed = true;
            _context = _previous;
            if (_ownsCaller) Monitor.Exit(Scheduler.CallerGate);
            _parentEntry?.Dispose();
        }
    }

    private static void QueueWorkers(BaseData data, IThreadPoolWorkItem work, int count)
    {
        if (count == 0) return;
        if (WorkerScheduler.Current is { } scheduler)
        {
            data.Scheduler = scheduler;
            scheduler.Enqueue(data.Queue = new(), work, count: count);
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
        int withdrawn = data.Scheduler is { } scheduler ? scheduler.Withdraw(data.Queue!) : data.WithdrawUnstarted();
        if (withdrawn > 0) data.MarkThreadCompleted(withdrawn);
        if (data.ActiveThreads > 0) data.Event.Wait();
    }
}
