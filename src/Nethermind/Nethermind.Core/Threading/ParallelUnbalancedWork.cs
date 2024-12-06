// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Threading;

/// <summary>
/// Provides methods to execute parallel loops efficiently for unbalanced workloads.
/// </summary>
public class ParallelUnbalancedWork : IThreadPoolWorkItem
{
    public static readonly ParallelOptions DefaultOptions = new()
    {
        // default to the number of processors
        MaxDegreeOfParallelism = Environment.ProcessorCount
    };

    private readonly Data _data;

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
    {
        int threads = parallelOptions.MaxDegreeOfParallelism > 0 ? parallelOptions.MaxDegreeOfParallelism : Environment.ProcessorCount;

        Data data = new(threads, fromInclusive, toExclusive, action);

        for (int i = 0; i < threads - 1; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(new ParallelUnbalancedWork(data), preferLocal: false);
        }

        new ParallelUnbalancedWork(data).Execute();

        // If there are still active threads, wait for them to complete
        if (data.ActiveThreads > 0)
        {
            data.Event.Wait();
        }
    }

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
        => InitProcessor<TLocal>.For(fromInclusive, toExclusive, parallelOptions, init, default, action, @finally);

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
        => InitProcessor<TLocal>.For(fromInclusive, toExclusive, parallelOptions, null, value, action, @finally);

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
        => InitProcessor<TLocal>.For(fromInclusive, toExclusive, parallelOptions, null, state, action);

    /// <summary>
    /// Initializes a new instance of the <see cref="ParallelUnbalancedWork"/> class.
    /// </summary>
    /// <param name="data">The shared data for the parallel work.</param>
    private ParallelUnbalancedWork(Data data)
    {
        _data = data;
    }

    /// <summary>
    /// Executes the parallel work item.
    /// </summary>
    public void Execute()
    {
        int i = _data.Index.GetNext();
        while (i < _data.ToExclusive)
        {
            _data.Action(i);
            // Get the next index
            i = _data.Index.GetNext();
        }

        // Signal that this thread has completed its work
        _data.MarkThreadCompleted();
    }

    /// <summary>
    /// Provides a thread-safe counter for sharing indices among threads.
    /// </summary>
    private class SharedCounter(int fromInclusive)
    {
        private PaddedValue _index = new(fromInclusive);

        /// <summary>
        /// Gets the next index in a thread-safe manner.
        /// </summary>
        /// <returns>The next index.</returns>
        public int GetNext() => Interlocked.Increment(ref _index.Value) - 1;

        [StructLayout(LayoutKind.Explicit, Size = 128)]
        private struct PaddedValue(int value)
        {
            [FieldOffset(64)]
            public int Value = value;
        }
    }

    /// <summary>
    /// Represents the base data shared among threads during parallel execution.
    /// </summary>
    private class BaseData(int threads, int fromInclusive, int toExclusive)
    {
        /// <summary>
        /// Gets the shared counter for indices.
        /// </summary>
        public SharedCounter Index { get; } = new SharedCounter(fromInclusive);
        public SemaphoreSlim Event { get; } = new(initialCount: 0);
        private int _activeThreads = threads;

        /// <summary>
        /// Gets the exclusive upper bound of the range.
        /// </summary>
        public int ToExclusive => toExclusive;

        /// <summary>
        /// Gets the number of active threads.
        /// </summary>
        public int ActiveThreads => Volatile.Read(ref _activeThreads);

        /// <summary>
        /// Marks a thread as completed.
        /// </summary>
        /// <returns>The number of remaining active threads.</returns>
        public int MarkThreadCompleted()
        {
            var remaining = Interlocked.Decrement(ref _activeThreads);

            if (remaining == 0)
            {
                Event.Release();
            }

            return remaining;
        }
    }

    /// <summary>
    /// Represents the data shared among threads for the parallel action.
    /// </summary>
    private class Data(int threads, int fromInclusive, int toExclusive, Action<int> action) :
        BaseData(threads, fromInclusive, toExclusive)
    {
        /// <summary>
        /// Gets the action to be executed for each iteration.
        /// </summary>
        public Action<int> Action => action;
    }

    /// <summary>
    /// Provides methods to execute parallel loops with thread-local data initialization and finalization.
    /// </summary>
    /// <typeparam name="TLocal">The type of the thread-local data.</typeparam>
    private class InitProcessor<TLocal> : IThreadPoolWorkItem
    {
        private readonly Data<TLocal> _data;

        /// <summary>
        /// Executes a parallel for loop over a range of integers, with thread-local data initialization and finalization.
        /// </summary>
        /// <param name="fromInclusive">The inclusive lower bound of the range.</param>
        /// <param name="toExclusive">The exclusive upper bound of the range.</param>
        /// <param name="parallelOptions">An object that configures the behavior of this operation.</param>
        /// <param name="init">The function to initialize the local data for each thread.</param>
        /// <param name="initValue">The initial value of the local data.</param>
        /// <param name="action">The delegate that is invoked once per iteration.</param>
        /// <param name="finally">The function to finalize the local data for each thread.</param>
        public static void For(
            int fromInclusive,
            int toExclusive,
            ParallelOptions parallelOptions,
            Func<TLocal>? init,
            TLocal? initValue,
            Func<int, TLocal, TLocal> action,
            Action<TLocal>? @finally = null)
        {
            // Determine the number of threads to use
            var threads = parallelOptions.MaxDegreeOfParallelism > 0
                ? parallelOptions.MaxDegreeOfParallelism
                : Environment.ProcessorCount;

            // Create shared data with thread-local initializers and finalizers
            var data = new Data<TLocal>(threads, fromInclusive, toExclusive, action, init, initValue, @finally);

            // Queue work items to the thread pool for all threads except the current one
            for (int i = 0; i < threads - 1; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(new InitProcessor<TLocal>(data), preferLocal: false);
            }

            // Execute work on the current thread
            new InitProcessor<TLocal>(data).Execute();

            // If there are still active threads, wait for them to complete
            if (data.ActiveThreads > 0)
            {
                data.Event.Wait();
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InitProcessor{TLocal}"/> class.
        /// </summary>
        /// <param name="data">The shared data for the parallel work.</param>
        private InitProcessor(Data<TLocal> data) => _data = data;

        /// <summary>
        /// Executes the parallel work item with thread-local data.
        /// </summary>
        public void Execute()
        {
            TLocal? value = _data.Init();
            int i = _data.Index.GetNext();
            while (i < _data.ToExclusive)
            {
                value = _data.Action(i, value);
                i = _data.Index.GetNext();
            }

            _data.Finally(value);

            _data.MarkThreadCompleted();
        }

        /// <summary>
        /// Represents the data shared among threads for the parallel action with thread-local data.
        /// </summary>
        /// <typeparam name="TValue">The type of the thread-local data.</typeparam>
        private class Data<TValue>(int threads,
            int fromInclusive,
            int toExclusive,
            Func<int, TLocal, TLocal> action,
            Func<TValue>? init = null,
            TValue? initValue = default,
            Action<TValue>? @finally = null) : BaseData(threads, fromInclusive, toExclusive)
        {
            /// <summary>
            /// Gets the action to be executed for each iteration.
            /// </summary>
            public Func<int, TLocal, TLocal> Action => action;

            /// <summary>
            /// Initializes the thread-local data.
            /// </summary>
            /// <returns>The initialized thread-local data.</returns>
            public TValue Init() => initValue ?? (init is not null ? init.Invoke() : default)!;

            /// <summary>
            /// Finalizes the thread-local data.
            /// </summary>
            /// <param name="value">The thread-local data to finalize.</param>
            public void Finally(TValue value)
            {
                @finally?.Invoke(value);
            }
        }
    }
}
