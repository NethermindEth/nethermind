// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace Nethermind.State.Flat.History.Walk;

/// <summary>
/// Runs the walk's work items on exactly <c>workers</c> threads. Work forked by a running item goes onto one shared
/// stack that every thread drains before it takes another item, so the children of one large subtree spread over all
/// threads instead of staying on the thread that found them.
/// </summary>
/// <remarks>
/// A thread waiting in <see cref="WalkJoin.Join"/> runs forked work instead of blocking, so no more than
/// <c>workers</c> pieces of work execute at once. Forked work that already holds rows is only handed off while a
/// thread is parked (<see cref="WalkJoin.TryFork"/>) and is taken before anything on the stack, so it never waits
/// behind other work with its rows resident. Helping nests at most <see cref="MaxHelpDepth"/> joins deep per thread.
/// </remarks>
internal sealed class WalkScheduler(int workers, CancellationToken token)
{
    private const int MaxHelpDepth = 16;

    [ThreadStatic]
    private static int t_helpDepth;

    private readonly object _lock = new();
    private readonly Stack<WalkFork> _forks = new();
    private readonly Queue<WalkFork> _handOffs = new();
    private Queue<Action> _items = new();
    private int _runningItems;
    private int _parkedWorkers;
    private bool _failed;

    public void Run(List<Action> items)
    {
        _items = new Queue<Action>(items);
        Task[] runners = new Task[Math.Max(1, workers)];
        for (int i = 0; i < runners.Length; i++)
        {
            runners[i] = Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        try
        {
            Task.WaitAll(runners);
        }
        catch (AggregateException e)
        {
            Exception first = e.InnerExceptions[0];
            foreach (Exception inner in e.InnerExceptions)
            {
                if (inner is OperationCanceledException) continue;

                first = inner;
                break;
            }

            ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    public void Push(WalkFork fork)
    {
        lock (_lock)
        {
            _forks.Push(fork);
            Monitor.PulseAll(_lock);
        }
    }

    public bool TryPushToParked(WalkFork fork)
    {
        lock (_lock)
        {
            if (_parkedWorkers <= _handOffs.Count) return false;

            _handOffs.Enqueue(fork);
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    public void HelpUntilDone(WalkJoin join)
    {
        bool help = t_helpDepth < MaxHelpDepth;
        t_helpDepth++;
        try
        {
            while (true)
            {
                WalkFork? fork = null;
                lock (_lock)
                {
                    while (!join.IsDone && !(help && TryPopFork(out fork))) Monitor.Wait(_lock);
                }

                if (fork is null) return;

                fork.Run();
            }
        }
        finally
        {
            t_helpDepth--;
        }
    }

    public void Signal()
    {
        lock (_lock)
        {
            Monitor.PulseAll(_lock);
        }
    }

    private void Work()
    {
        while (TryTake(out WalkFork? fork, out Action? item))
        {
            if (fork is not null)
            {
                fork.Run();
                continue;
            }

            bool succeeded = false;
            try
            {
                token.ThrowIfCancellationRequested();
                item!();
                succeeded = true;
            }
            finally
            {
                lock (_lock)
                {
                    _runningItems--;
                    _failed |= !succeeded;
                    Monitor.PulseAll(_lock);
                }
            }
        }
    }

    private bool TryTake(out WalkFork? fork, out Action? item)
    {
        item = null;
        lock (_lock)
        {
            while (true)
            {
                if (TryPopFork(out fork)) return true;

                if (!_failed && _items.TryDequeue(out item))
                {
                    _runningItems++;
                    return true;
                }

                if (_runningItems == 0) return false;

                _parkedWorkers++;
                Monitor.Wait(_lock);
                _parkedWorkers--;
            }
        }
    }

    private bool TryPopFork([NotNullWhen(true)] out WalkFork? fork) => _handOffs.TryDequeue(out fork) || _forks.TryPop(out fork);
}

/// <summary>
/// The forks of one owner thread. The owner runs whichever of its forks no other thread has claimed yet, in the order
/// it forked them, then helps with other forked work until the last of its own has finished.
/// </summary>
internal sealed class WalkJoin(WalkScheduler scheduler)
{
    private readonly List<WalkFork> _forks = [];
    private int _pending;
    private Exception? _failure;
    private bool _failed;

    public bool Failed => Volatile.Read(ref _failed);

    public bool IsDone => Volatile.Read(ref _pending) == 0;

    public void Fork(Action work) => scheduler.Push(Add(work));

    public bool TryFork(Action work)
    {
        WalkFork fork = Add(work);
        if (scheduler.TryPushToParked(fork)) return true;

        _forks.RemoveAt(_forks.Count - 1);
        Interlocked.Decrement(ref _pending);
        return false;
    }

    public void ThrowIfFailed()
    {
        Exception? failure = Volatile.Read(ref _failure);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void Join(Exception? primary)
    {
        if (primary is not null) Volatile.Write(ref _failed, true);
        foreach (WalkFork fork in _forks) fork.Run();
        scheduler.HelpUntilDone(this);

        Exception? forked = Volatile.Read(ref _failure);
        Exception? failure = primary;
        if (primary is null || (primary is OperationCanceledException && forked is not null and not OperationCanceledException)) failure = forked ?? primary;
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void Fail(Exception failure)
    {
        Interlocked.CompareExchange(ref _failure, failure, null);
        Volatile.Write(ref _failed, true);
    }

    public void Finished()
    {
        if (Interlocked.Decrement(ref _pending) == 0) scheduler.Signal();
    }

    private WalkFork Add(Action work)
    {
        WalkFork fork = new(this, work);
        _forks.Add(fork);
        Interlocked.Increment(ref _pending);
        return fork;
    }
}

internal sealed class WalkFork(WalkJoin join, Action work)
{
    private int _claimed;

    public void Run()
    {
        if (Interlocked.Exchange(ref _claimed, 1) != 0) return;

        try
        {
            work();
        }
        catch (Exception e)
        {
            join.Fail(e);
        }
        finally
        {
            join.Finished();
        }
    }
}
