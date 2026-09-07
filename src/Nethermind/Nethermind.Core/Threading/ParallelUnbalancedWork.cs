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
