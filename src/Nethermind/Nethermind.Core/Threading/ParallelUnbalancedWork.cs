// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Threading;

/// <summary>
/// Provides methods to execute parallel loops efficiently for unbalanced workloads.
/// </summary>
public class ParallelUnbalancedWork : IThreadPoolWorkItem
{
    public static readonly ParallelOptions DefaultOptions = new() { MaxDegreeOfParallelism = Cpu.RuntimeInformation.ProcessorCount };
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

        Data data = new(threads, fromInclusive, toExclusive, action, parallelOptions.CancellationToken);

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

        // Rethrow the first captured worker exception, if any, on the calling thread
        data.ThrowIfFaulted();

        parallelOptions.CancellationToken.ThrowIfCancellationRequested();
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
    /// Executes a parallel for loop over a range of integers on dedicated threads at the given priority.
    /// Use for latency-critical work that must not queue behind thread-pool load (the shared pool
    /// gives no scheduling guarantees, so a saturated pool can delay individual iterations arbitrarily).
    /// </summary>
    /// <typeparam name="TLocal">The type of the thread-local data.</typeparam>
    /// <param name="fromInclusive">The inclusive lower bound of the range.</param>
    /// <param name="toExclusive">The exclusive upper bound of the range.</param>
    /// <param name="parallelOptions">An object that configures the behavior of this operation.</param>
    /// <param name="state">The initial state of the thread-local data.</param>
    /// <param name="action">The delegate that is invoked once per iteration.</param>
    /// <param name="workerPriority">The priority the dedicated worker threads run at.</param>
    public static void For<TLocal>(
        int fromInclusive,
        int toExclusive,
        ParallelOptions parallelOptions,
        TLocal state,
        Func<int, TLocal, TLocal> action,
        ThreadPriority workerPriority)
        => InitProcessor<TLocal>.ForDedicated(fromInclusive, toExclusive, parallelOptions, null, state, action, null, workerPriority);

    /// <summary>
    /// Initializes a new instance of the <see cref="ParallelUnbalancedWork"/> class.
    /// </summary>
    /// <param name="data">The shared data for the parallel work.</param>
    private ParallelUnbalancedWork(Data data) => _data = data;

    /// <summary>
    /// Executes the parallel work item.
    /// </summary>
    public void Execute()
    {
        try
        {
            try
            {
                int i = _data.Index.GetNext();
                while (i < _data.ToExclusive)
                {
                    // Stop pulling work once cancelled or another worker has faulted.
                    if (_data.CancellationToken.IsCancellationRequested || _data.IsFaulted) return;
                    _data.Action(i);
                    i = _data.Index.GetNext();
                }
            }
            catch (Exception ex)
            {
                // Capture so the exception is rethrown on the calling thread instead of escaping
                // a thread-pool worker (which would otherwise be unobserved/fatal).
                _data.CaptureException(ex);
            }
        }
        finally
        {
            // Signal that this thread has completed its work
            _data.MarkThreadCompleted();
        }
    }

    /// <summary>
    /// A small pool of persistent worker threads for <c>ForDedicated</c> bursts. Workers are
    /// created once and parked on a semaphore, so a burst pays a wake-up (~tens of microseconds)
    /// instead of thread creation (~milliseconds under load). Priority is applied per work item
    /// and restored afterwards. Intended for short CPU-bound bursts only.
    /// </summary>
    private static class DedicatedWorkers
    {
        private static readonly ConcurrentQueue<(IThreadPoolWorkItem Item, ThreadPriority Priority)> _queue = new();
        private static readonly SemaphoreSlim _signal = new(0);
        private static int _started;

        public static void Post(IThreadPoolWorkItem item, ThreadPriority priority)
        {
            EnsureStarted();
            _queue.Enqueue((item, priority));
            _signal.Release();
        }

        private static void EnsureStarted()
        {
            if (Volatile.Read(ref _started) != 0) return;
            if (Interlocked.Exchange(ref _started, 1) != 0) return;

            int workers = Math.Max(1, Cpu.RuntimeInformation.ProcessorCount - 1);
            for (int i = 0; i < workers; i++)
            {
                Thread worker = new(Run)
                {
                    IsBackground = true,
                    Name = $"{nameof(ParallelUnbalancedWork)}.{nameof(DedicatedWorkers)}",
                };
                worker.Start();
            }
        }

        private static void Run()
        {
            Thread currentThread = Thread.CurrentThread;
            while (true)
            {
                _signal.Wait();
                if (!_queue.TryDequeue(out (IThreadPoolWorkItem Item, ThreadPriority Priority) work)) continue;

                currentThread.Priority = work.Priority;
                try
                {
                    // Work items capture their own exceptions (rethrown on the caller); nothing escapes here.
                    work.Item.Execute();
                }
                finally
                {
                    currentThread.Priority = ThreadPriority.Normal;
                }
            }
        }
    }

    /// <summary>
    /// Provides a thread-safe counter for sharing indices among threads.
    /// </summary>
    private class SharedCounter(int fromInclusive)
    {
        private CacheLinePaddedLong _index = new(fromInclusive);

        /// <summary>
        /// Gets the next index in a thread-safe manner.
        /// </summary>
        /// <returns>The next index.</returns>
        public int GetNext() => (int)(Interlocked.Increment(ref _index.Value) - 1);
    }

    /// <summary>
    /// Represents the base data shared among threads during parallel execution.
    /// </summary>
    private class BaseData(int threads, int fromInclusive, int toExclusive, CancellationToken token)
    {
        /// <summary>
        /// Gets the shared counter for indices.
        /// </summary>
        public SharedCounter Index { get; } = new SharedCounter(fromInclusive);

        public ManualResetEventSlim Event { get; } = new(initialState: false);
        private int _activeThreads = threads;
        private int _faulted;
        private ExceptionDispatchInfo? _exception;
        public CancellationToken CancellationToken { get; } = token;

        /// <summary>
        /// Gets the exclusive upper bound of the range.
        /// </summary>
        public int ToExclusive => toExclusive;

        /// <summary>
        /// Gets the number of active threads.
        /// </summary>
        public int ActiveThreads => Volatile.Read(ref _activeThreads);

        /// <summary>
        /// Whether any worker has captured an exception. Used by workers to short-circuit
        /// fetching new indices once the operation is already faulted.
        /// </summary>
        public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

        /// <summary>
        /// Captures the first exception observed by any worker so it can be rethrown on the
        /// calling thread. Subsequent exceptions are dropped.
        /// </summary>
        public void CaptureException(Exception exception)
        {
            // Publish the fault flag before the (non-trivial) ExceptionDispatchInfo.Capture so
            // other workers can short-circuit during the capture window.
            if (Interlocked.CompareExchange(ref _faulted, 1, 0) != 0) return;
            Volatile.Write(ref _exception, ExceptionDispatchInfo.Capture(exception));
        }

        /// <summary>
        /// Rethrows the first captured exception (preserving its original stack trace), if any.
        /// </summary>
        public void ThrowIfFaulted() => Volatile.Read(ref _exception)?.Throw();

        /// <summary>
        /// Marks a thread as completed.
        /// </summary>
        /// <returns>The number of remaining active threads.</returns>
        public int MarkThreadCompleted()
        {
            int remaining = Interlocked.Decrement(ref _activeThreads);

            if (remaining == 0)
            {
                Event.Set();
            }

            return remaining;
        }
    }

    /// <summary>
    /// Represents the data shared among threads for the parallel action.
    /// </summary>
    private class Data(int threads, int fromInclusive, int toExclusive, Action<int> action, CancellationToken token) :
        BaseData(threads, fromInclusive, toExclusive, token)
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
            int threads = parallelOptions.MaxDegreeOfParallelism > 0
                ? parallelOptions.MaxDegreeOfParallelism
                : Environment.ProcessorCount;

            // Create shared data with thread-local initializers and finalizers
            Data<TLocal> data = new(threads, fromInclusive, toExclusive, action, init, initValue, @finally, parallelOptions.CancellationToken);

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

            // Rethrow the first captured worker exception, if any, on the calling thread
            data.ThrowIfFaulted();

            parallelOptions.CancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Executes a parallel for loop over a range of integers on dedicated threads at the given
        /// priority instead of the shared thread pool. The calling thread participates with its
        /// priority temporarily raised to match.
        /// </summary>
        /// <param name="fromInclusive">The inclusive lower bound of the range.</param>
        /// <param name="toExclusive">The exclusive upper bound of the range.</param>
        /// <param name="parallelOptions">An object that configures the behavior of this operation.</param>
        /// <param name="init">The function to initialize the local data for each thread.</param>
        /// <param name="initValue">The initial value of the local data.</param>
        /// <param name="action">The delegate that is invoked once per iteration.</param>
        /// <param name="finally">The function to finalize the local data for each thread.</param>
        /// <param name="workerPriority">The priority the dedicated worker threads run at.</param>
        public static void ForDedicated(
            int fromInclusive,
            int toExclusive,
            ParallelOptions parallelOptions,
            Func<TLocal>? init,
            TLocal? initValue,
            Func<int, TLocal, TLocal> action,
            Action<TLocal>? @finally,
            ThreadPriority workerPriority)
        {
            int threads = parallelOptions.MaxDegreeOfParallelism > 0
                ? parallelOptions.MaxDegreeOfParallelism
                : Environment.ProcessorCount;

            Data<TLocal> data = new(threads, fromInclusive, toExclusive, action, init, initValue, @finally, parallelOptions.CancellationToken);

            for (int i = 0; i < threads - 1; i++)
            {
                DedicatedWorkers.Post(new InitProcessor<TLocal>(data), workerPriority);
            }

            Thread currentThread = Thread.CurrentThread;
            ThreadPriority previousPriority = currentThread.Priority;
            currentThread.Priority = workerPriority;
            try
            {
                new InitProcessor<TLocal>(data).Execute();

                if (data.ActiveThreads > 0)
                {
                    data.Event.Wait();
                }
            }
            finally
            {
                currentThread.Priority = previousPriority;
            }

            data.ThrowIfFaulted();

            parallelOptions.CancellationToken.ThrowIfCancellationRequested();
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
            TLocal? value = default;
            // Track Init success so a throwing Init does not leak into Finally with default(TLocal)
            // — matches BCL Parallel.For<TLocal>, which only invokes localFinally when localInit ran.
            bool initSucceeded = false;
            try
            {
                value = _data.Init();
                initSucceeded = true;
                int i = _data.Index.GetNext();
                while (i < _data.ToExclusive)
                {
                    // Stop pulling work once cancelled or another worker has faulted.
                    if (_data.CancellationToken.IsCancellationRequested || _data.IsFaulted) return;
                    value = _data.Action(i, value);
                    i = _data.Index.GetNext();
                }
            }
            catch (Exception ex)
            {
                // Capture so the exception is rethrown on the calling thread instead of escaping
                // a thread-pool worker (which would otherwise be unobserved/fatal).
                _data.CaptureException(ex);
            }
            finally
            {
                if (initSucceeded)
                {
                    // A throwing Finally must not skip MarkThreadCompleted, or the calling thread
                    // hangs on the semaphore. Capture and continue.
                    try
                    {
                        _data.Finally(value!);
                    }
                    catch (Exception ex)
                    {
                        _data.CaptureException(ex);
                    }
                }
                _data.MarkThreadCompleted();
            }
        }

        /// <summary>
        /// Represents the data shared among threads for the parallel action with thread-local data.
        /// </summary>
        /// <typeparam name="TValue">The type of the thread-local data.</typeparam>
        private class Data<TValue>(int threads,
            int fromInclusive,
            int toExclusive,
            Func<int, TLocal, TLocal> action,
            Func<TValue>? init,
            TValue? initValue,
            Action<TValue>? @finally,
            CancellationToken token) : BaseData(threads, fromInclusive, toExclusive, token)
        {
            /// <summary>
            /// Gets the action to be executed for each iteration.
            /// </summary>
            public Func<int, TLocal, TLocal> Action => action;

            /// <summary>
            /// Initializes the thread-local data.
            /// </summary>
            /// <returns>The initialized thread-local data.</returns>
            public TValue Init() => init is not null ? init.Invoke() : initValue!;

            /// <summary>
            /// Finalizes the thread-local data.
            /// </summary>
            /// <param name="value">The thread-local data to finalize.</param>
            public void Finally(TValue value) => @finally?.Invoke(value);
        }
    }
}
