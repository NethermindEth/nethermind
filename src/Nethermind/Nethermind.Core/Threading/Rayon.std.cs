// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Threading;

public static partial class Rayon
{
    public static partial int WorkerCount => Registry.Instance.Workers.Length;

    public static partial bool IsWorkerThread => Worker.Current is not null;

    private static partial (TA, TB) JoinCore<TSa, TSb, TA, TB>(in TSa aState, Func<TSa, bool, TA> a, in TSb bState, Func<TSb, bool, TB> b)
    {
        Worker? worker = Worker.Current;
        return worker is null
            ? JoinCold(in aState, a, in bState, b)
            : JoinOnWorker(worker, in aState, a, in bState, b);
    }

    private static (TA, TB) JoinOnWorker<TSa, TSb, TA, TB>(Worker worker, in TSa aState, Func<TSa, bool, TA> a, in TSb bState, Func<TSb, bool, TB> b)
    {
        StackJob<TSb, TB> jobB = new(in bState, b, worker);
        worker.Push(jobB);

        TA resultA;
        try
        {
            resultA = a(aState, false);
        }
        catch
        {
            // b may reference the caller's frame through its state, so it must finish before unwinding.
            // WaitUntil pops b itself if nobody stole it; b's result and exception are discarded.
            worker.WaitUntil(jobB.Latch);
            throw;
        }

        // a may have executed b already (nested join) — the latch says so. Otherwise pop it back; a thief
        // may have taken it, in which case the deque yields older jobs of enclosing joins (run them, their
        // owners are further up this stack) or nothing (then help until b's latch is set).
        while (!jobB.Latch.IsSet)
        {
            Job? popped = worker.Deque.Pop();
            if (popped is null)
            {
                worker.WaitUntil(jobB.Latch);
                break;
            }

            if (ReferenceEquals(popped, jobB))
            {
                return (resultA, jobB.RunInline());
            }

            popped.Execute();
        }

        return (resultA, jobB.IntoResult());
    }

    private static (TA, TB) JoinCold<TSa, TSb, TA, TB>(in TSa aState, Func<TSa, bool, TA> a, in TSb bState, Func<TSb, bool, TB> b)
    {
        ColdJoinState<TSa, TSb, TA, TB> state = new(aState, a, bState, b);

        // A free context lets the caller take part instead of handing everything over and blocking.
        Worker? worker = Registry.Instance.TryAcquire();
        if (worker is not null)
        {
            return worker.RunAsCurrent(in state, static (w, s) => JoinOnWorker(w, s.AState, s.A, s.BState, s.B));
        }

        StackJob<ColdJoinState<TSa, TSb, TA, TB>, (TA, TB)> job = new(
            in state,
            static (s, _) => JoinOnWorker(Worker.Current!, s.AState, s.A, s.BState, s.B),
            owner: null);
        Registry.Instance.Inject(job);
        job.Latch.WaitCold();
        return job.IntoResult();
    }

    private readonly record struct ColdJoinState<TSa, TSb, TA, TB>(TSa AState, Func<TSa, bool, TA> A, TSb BState, Func<TSb, bool, TB> B);

    private static partial void ForCore<TState>(int fromInclusive, int toExclusive, in TState state, Action<TState, int> body) =>
        ForRange(new ForState<TState>(state, body, fromInclusive, toExclusive, new Splitter(WorkerCount)), migrated: false);

    private static int ForRange<TState>(in ForState<TState> state, bool migrated)
    {
        int length = state.To - state.From;
        Splitter splitter = state.Splitter;
        if (length > 1 && splitter.TrySplit(migrated))
        {
            int mid = state.From + length / 2;
            JoinCore(
                state with { To = mid, Splitter = splitter }, static (half, m) => ForRange(in half, m),
                state with { From = mid, Splitter = splitter }, static (half, m) => ForRange(in half, m));
            return 0;
        }

        for (int i = state.From; i < state.To; i++)
        {
            state.Body(state.State, i);
        }

        return 0;
    }

    private readonly record struct ForState<TState>(TState State, Action<TState, int> Body, int From, int To, Splitter Splitter);

    /// <summary>
    /// rayon's adaptive splitter: split about twice per worker up front, and re-split a stolen job since
    /// theft means an idle thread exists.
    /// </summary>
    private struct Splitter(int splits)
    {
        private int _splits = splits;

        public bool TrySplit(bool migrated)
        {
            if (migrated)
            {
                _splits = Math.Max(WorkerCount, _splits / 2);
                return true;
            }

            if (_splits > 0)
            {
                _splits /= 2;
                return true;
            }

            return false;
        }
    }
}
