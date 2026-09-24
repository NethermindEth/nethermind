// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Core.Threading;

/// <summary>
/// Provides methods to execute parallel loops efficiently for unbalanced workloads.
/// </summary>
/// <remarks>
/// The loop bodies live in <c>ParallelUnbalancedWork.std.cs</c>, which spreads the range over
/// thread-pool workers, and in <c>ParallelUnbalancedWork.zkevm.cs</c>, which runs it on the calling
/// thread: the zkEVM guest has no threads, and an ahead-of-time build would otherwise carry the
/// thread pool for loops that never fan out.
/// </remarks>
public partial class ParallelUnbalancedWork
{
    /// <summary>Shares a worker budget between parallel operations on the calling thread and their nested work.</summary>
    /// <remarks>
    /// The budget includes the calling thread. Nested scopes inherit the outer budget.
    /// This is a synchronous, thread-affine scope; join or dispose its background work before leaving it.
    /// Independent callers and unrelated thread-pool work are outside this budget.
    /// </remarks>
    /// <param name="maxDegreeOfParallelism">The maximum number of participating threads, including the caller.</param>
    /// <returns>A scope that restores the previous worker context on disposal.</returns>
    public static WorkerScope BeginWorkerScope(int maxDegreeOfParallelism)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);
        return new(maxDegreeOfParallelism);
    }

    /// <summary>Provides a shared worker budget for synchronous and background parallel operations.</summary>
    public sealed partial class WorkerScope : IDisposable
    {
        /// <summary>Restores the calling thread's previous worker context.</summary>
        public partial void Dispose();
    }

    internal static partial int GetWorkerCount(int fromInclusive, int toExclusive, ParallelOptions parallelOptions);

    public static readonly ParallelOptions DefaultOptions = new() { MaxDegreeOfParallelism = Cpu.RuntimeInformation.ProcessorCount };

    /// <summary>
    /// Executes a parallel for loop over a range of integers.
    /// </summary>
    /// <param name="fromInclusive">The inclusive lower bound of the range.</param>
    /// <param name="toExclusive">The exclusive upper bound of the range.</param>
    /// <param name="action">The delegate that is invoked once per iteration.</param>
    public static void For(int fromInclusive, int toExclusive, Action<int> action)
        => For(fromInclusive, toExclusive, DefaultOptions, action);

    /// <summary>
    /// Executes a parallel for loop over a range of integers, with the specified options.
    /// </summary>
    /// <param name="fromInclusive">The inclusive lower bound of the range.</param>
    /// <param name="toExclusive">The exclusive upper bound of the range.</param>
    /// <param name="parallelOptions">An object that configures the behavior of this operation.</param>
    /// <param name="action">The delegate that is invoked once per iteration.</param>
    public static void For(int fromInclusive, int toExclusive, ParallelOptions parallelOptions, Action<int> action)
        => ForCore(fromInclusive, toExclusive, parallelOptions, action);

    /// <summary>
    /// Executes a parallel for loop over a range of integers, with thread-local data, initialization, and finalization functions.
    /// </summary>
    /// <typeparam name="TLocal">The type of the thread-local data.</typeparam>
    /// <param name="fromInclusive">The inclusive lower bound of the range.</param>
    /// <param name="toExclusive">The exclusive upper bound of the range.</param>
    /// <param name="parallelOptions">An object that configures the behavior of this operation.</param>
    /// <param name="init">The function to initialize the local data for each thread.</param>
    /// <param name="action">The delegate that is invoked once per iteration.</param>
    /// <param name="finally">The function to finalize the local data for each thread.</param>
    public static void For<TLocal>(
        int fromInclusive,
        int toExclusive,
        ParallelOptions parallelOptions,
        Func<TLocal> init,
        Func<int, TLocal, TLocal> action,
        Action<TLocal> @finally)
        => ForCore(fromInclusive, toExclusive, parallelOptions, init, default, action, @finally);

    /// <summary>
    /// Executes a parallel for loop over a range of integers, with thread-local data, initialization, and finalization functions.
    /// </summary>
    /// <typeparam name="TLocal">The type of the thread-local data.</typeparam>
    /// <param name="fromInclusive">The inclusive lower bound of the range.</param>
    /// <param name="toExclusive">The exclusive upper bound of the range.</param>
    /// <param name="parallelOptions">An object that configures the behavior of this operation.</param>
    /// <param name="value">The initial the local data for each thread.</param>
    /// <param name="action">The delegate that is invoked once per iteration.</param>
    /// <param name="finally">The function to finalize the local data for each thread.</param>
    public static void For<TLocal>(
        int fromInclusive,
        int toExclusive,
        ParallelOptions parallelOptions,
        TLocal value,
        Func<int, TLocal, TLocal> action,
        Action<TLocal> @finally)
        => ForCore(fromInclusive, toExclusive, parallelOptions, null, value, action, @finally);

    /// <summary>
    /// Executes a parallel for loop over a range of integers, with thread-local data.
    /// </summary>
    /// <typeparam name="TLocal">The type of the thread-local data.</typeparam>
    /// <param name="fromInclusive">The inclusive lower bound of the range.</param>
    /// <param name="toExclusive">The exclusive upper bound of the range.</param>
    /// <param name="state">The initial state of the thread-local data.</param>
    /// <param name="action">The delegate that is invoked once per iteration.</param>
    public static void For<TLocal>(int fromInclusive, int toExclusive, TLocal state, Func<int, TLocal, TLocal> action)
        => For(fromInclusive, toExclusive, DefaultOptions, state, action);

    /// <summary>
    /// Executes a parallel for loop over a range of integers, with thread-local data and specified options.
    /// </summary>
    /// <typeparam name="TLocal">The type of the thread-local data.</typeparam>
    /// <param name="fromInclusive">The inclusive lower bound of the range.</param>
    /// <param name="toExclusive">The exclusive upper bound of the range.</param>
    /// <param name="parallelOptions">An object that configures the behavior of this operation.</param>
    /// <param name="state">The initial state of the thread-local data.</param>
    /// <param name="action">The delegate that is invoked once per iteration.</param>
    public static void For<TLocal>(
        int fromInclusive,
        int toExclusive,
        ParallelOptions parallelOptions,
        TLocal state,
        Func<int, TLocal, TLocal> action)
        => ForCore(fromInclusive, toExclusive, parallelOptions, null, state, action, null);

    /// <summary>Starts loop workers without executing iterations on the caller until it joins.</summary>
    /// <remarks>
    /// The worker limit includes the joining caller: a limit of one defers all work until joining.
    /// The zkEVM implementation always defers work until joining.
    /// Dispose abandons unclaimed iterations and waits for running callbacks without reporting faults;
    /// WaitForCompletion reports them. Join and disposal must be called sequentially, outside callbacks.
    /// The completion callback runs once on success, including an empty range, on the joiner or last worker.
    /// Keep callback state alive until joining or disposal returns. Joining after abandonment throws ObjectDisposedException.
    /// </remarks>
    /// <param name="fromInclusive">The inclusive lower bound of the range.</param>
    /// <param name="toExclusive">The exclusive upper bound of the range.</param>
    /// <param name="options">The worker limit and cancellation token.</param>
    /// <param name="action">The callback for each iteration.</param>
    /// <param name="completed">An optional callback after all iterations succeed.</param>
    /// <returns>A handle that must be joined or disposed before releasing callback state.</returns>
    public static BackgroundWork BackgroundFor(int fromInclusive, int toExclusive, ParallelOptions options,
        Action<int> action, Action? completed = null)
        => BackgroundForCore(fromInclusive, toExclusive, options, action, completed);

    private static partial BackgroundWork BackgroundForCore(int fromInclusive, int toExclusive,
        ParallelOptions options, Action<int> action, Action? completed);

    /// <summary>Coordinates background iterations and their completion callback.</summary>
    public sealed partial class BackgroundWork : IDisposable
    {
        /// <summary>Runs a callback after this stage succeeds, returning a handle for the whole chain.</summary>
        /// <remarks>Each stage accepts one continuation. Join and dispose the returned handle to drain all stages.</remarks>
        /// <param name="action">The callback to run after this stage succeeds.</param>
        /// <returns>The final stage, which owns the preceding stages for joining and disposal.</returns>
        public BackgroundWork ContinueWith(Action action)
            => ContinueWith(0, 1, DefaultOptions, _ => action());

        /// <summary>Starts another parallel loop after this stage succeeds.</summary>
        /// <remarks>
        /// Each stage accepts one continuation. Joining the returned handle helps finish preceding stages first;
        /// disposing it abandons pending work and drains running callbacks throughout the chain.
        /// Attach, join and dispose from a single owner, outside callbacks. Faults or cancellation skip subsequent stages.
        /// </remarks>
        /// <param name="fromInclusive">The inclusive lower bound of the next range.</param>
        /// <param name="toExclusive">The exclusive upper bound of the next range.</param>
        /// <param name="options">The next stage's worker limit and cancellation token.</param>
        /// <param name="action">The callback for each iteration of the next stage.</param>
        /// <param name="completed">An optional callback after the next stage's iterations succeed.</param>
        /// <returns>The final stage, which owns the preceding stages for joining and disposal.</returns>
        public BackgroundWork ContinueWith(int fromInclusive, int toExclusive, ParallelOptions options,
            Action<int> action, Action? completed = null)
            => ContinueWithCore(fromInclusive, toExclusive, options, action, completed);

        private partial BackgroundWork ContinueWithCore(int fromInclusive, int toExclusive, ParallelOptions options,
            Action<int> action, Action? completed);

        /// <summary>Helps execute outstanding iterations, waits for completion, and reports faults or cancellation.</summary>
        public partial void WaitForCompletion();

        /// <summary>Abandons unclaimed work and waits for running callbacks without throwing captured faults or cancellation.</summary>
        public partial void Dispose();
    }

    private static partial void ForCore(int fromInclusive, int toExclusive, ParallelOptions parallelOptions, Action<int> action);

    private static partial void ForCore<TLocal>(
        int fromInclusive,
        int toExclusive,
        ParallelOptions parallelOptions,
        Func<TLocal>? init,
        TLocal? initValue,
        Func<int, TLocal, TLocal> action,
        Action<TLocal>? @finally);
}
