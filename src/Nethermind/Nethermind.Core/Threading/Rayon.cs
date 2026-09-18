// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Threading;

/// <summary>
/// Fork-join parallelism over a process-wide work-stealing pool, after Rust's rayon-core.
/// </summary>
/// <remarks>
/// Each pool thread owns a LIFO deque; <see cref="Join{TSa,TSb,TA,TB}"/> pushes its second operand there,
/// runs the first inline and then either pops the second back or, when a thief took it, helps with other
/// work until it completes. Callers that are not pool threads inject the join and block until it is done.
/// Fork cost is one small heap object, so recursive splitting down to a few hundred entries is cheap.
/// On the zkVM guest both operands run sequentially on the caller.
/// </remarks>
public static partial class Rayon
{
    /// <summary>Number of pool threads.</summary>
    public static partial int WorkerCount { get; }

    /// <summary>Whether the current thread is a pool thread.</summary>
    public static partial bool IsWorkerThread { get; }

    /// <summary>
    /// Runs <paramref name="a"/> and <paramref name="b"/> potentially in parallel and returns both results.
    /// </summary>
    /// <remarks>
    /// An exception from either operand is rethrown to the caller. When <paramref name="a"/> throws while
    /// <paramref name="b"/> is running on another thread, the call still waits for <paramref name="b"/> to
    /// finish before rethrowing, and <paramref name="b"/>'s own exception, if any, is discarded.
    /// </remarks>
    public static (TA, TB) Join<TA, TB>(Func<TA> a, Func<TB> b) =>
        JoinCore(a, static (func, _) => func(), b, static (func, _) => func());

    /// <inheritdoc cref="Join{TA,TB}"/>
    public static void Join(Action a, Action b) =>
        JoinCore(a, static (action, _) => Run(action), b, static (action, _) => Run(action));

    /// <summary>
    /// <inheritdoc cref="Join{TA,TB}" path="/summary"/>
    /// The state overloads let a static lambda carry its inputs by value instead of allocating a closure.
    /// </summary>
    /// <inheritdoc cref="Join{TA,TB}" path="/remarks"/>
    public static (TA, TB) Join<TSa, TSb, TA, TB>(in TSa aState, Func<TSa, TA> a, in TSb bState, Func<TSb, TB> b) =>
        JoinCore(
            new FuncState<TSa, TA>(aState, a), static (state, _) => state.Func(state.State),
            new FuncState<TSb, TB>(bState, b), static (state, _) => state.Func(state.State));

    /// <inheritdoc cref="Join{TSa,TSb,TA,TB}"/>
    public static void Join<TSa, TSb>(in TSa aState, Action<TSa> a, in TSb bState, Action<TSb> b) =>
        JoinCore(
            new ActionState<TSa>(aState, a), static (state, _) => Run(state),
            new ActionState<TSb>(bState, b), static (state, _) => Run(state));

    /// <summary>
    /// Runs <paramref name="body"/> for every index in [<paramref name="fromInclusive"/>, <paramref name="toExclusive"/>),
    /// splitting the range recursively with <see cref="Join{TSa,TSb,TA,TB}"/>.
    /// </summary>
    /// <remarks>
    /// The range is split about twice per worker up front; a half that gets stolen is split again, so
    /// uneven per-index cost is balanced dynamically.
    /// </remarks>
    public static void For(int fromInclusive, int toExclusive, Action<int> body) =>
        For(fromInclusive, toExclusive, body, static (action, i) => action(i));

    /// <inheritdoc cref="For(int,int,Action{int})"/>
    public static void For<TState>(int fromInclusive, int toExclusive, in TState state, Action<TState, int> body)
    {
        if (toExclusive - fromInclusive <= 0)
        {
            return;
        }

        ForCore(fromInclusive, toExclusive, in state, body);
    }

    private readonly record struct FuncState<TState, TResult>(TState State, Func<TState, TResult> Func);

    private readonly record struct ActionState<TState>(TState State, Action<TState> Action);

    private static int Run(Action action)
    {
        action();
        return 0;
    }

    private static int Run<TState>(in ActionState<TState> state)
    {
        state.Action(state.State);
        return 0;
    }

    /// <param name="a">Receives the state and whether it runs on a thread other than the caller's.</param>
    private static partial (TA, TB) JoinCore<TSa, TSb, TA, TB>(in TSa aState, Func<TSa, bool, TA> a, in TSb bState, Func<TSb, bool, TB> b);

    private static partial void ForCore<TState>(int fromInclusive, int toExclusive, in TState state, Action<TState, int> body);
}
