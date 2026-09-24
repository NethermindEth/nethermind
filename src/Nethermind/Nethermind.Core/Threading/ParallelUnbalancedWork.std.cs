// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Threading;

public partial class ParallelUnbalancedWork : IThreadPoolWorkItem
{
    private readonly Data _data;

    private static partial void ForCore(int fromInclusive, int toExclusive, ParallelOptions parallelOptions, Action<int> action)
    {
        int threads = GetWorkerCount(fromInclusive, toExclusive, parallelOptions);
        if (threads == 0) return;

        Data data = new(threads, fromInclusive, toExclusive, action, parallelOptions.CancellationToken);

        // Workers hold no state of their own, so one instance serves every slot.
        ParallelUnbalancedWork worker = new(data);
        QueueWorkers(data, worker, threads - 1);

        worker.Execute();

        // If there are still active threads, wait for them to complete
        if (data.ActiveThreads > 0)
        {
            WaitForWorkers(data);
        }

        // Rethrow the first captured worker exception, if any, on the calling thread
        data.ThrowIfFaulted();

        parallelOptions.CancellationToken.ThrowIfCancellationRequested();
    }

    private static partial void ForCore<TLocal>(
        int fromInclusive,
        int toExclusive,
        ParallelOptions parallelOptions,
        Func<TLocal>? init,
        TLocal? initValue,
        Func<int, TLocal, TLocal> action,
        Action<TLocal>? @finally)
        => InitProcessor<TLocal>.For(fromInclusive, toExclusive, parallelOptions, init, initValue, action, @finally);

    internal static partial int GetWorkerCount(int fromInclusive, int toExclusive, ParallelOptions parallelOptions)
    {
        parallelOptions.CancellationToken.ThrowIfCancellationRequested();

        long rangeLength = (long)toExclusive - fromInclusive;
        if (rangeLength <= 0) return 0;

        int maxWorkers = parallelOptions.MaxDegreeOfParallelism > 0
            ? parallelOptions.MaxDegreeOfParallelism
            : Environment.ProcessorCount;

        if (WorkerScope.Current is { } scope) maxWorkers = Math.Min(maxWorkers, scope.Concurrency);
        return (int)Math.Min(rangeLength, maxWorkers);
    }

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

        /// <summary>The scope and queue holding the unstarted workers, set by the caller when it queues them in a scope.</summary>
        internal WorkerScope? Scope;
        internal WorkerScope.WorkQueue? Queue;
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
        /// <param name="count">The number of threads completing together.</param>
        /// <returns>The number of remaining active threads.</returns>
        public int MarkThreadCompleted(int count = 1)
        {
            int remaining = Interlocked.Add(ref _activeThreads, -count);

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
            int threads = GetWorkerCount(fromInclusive, toExclusive, parallelOptions);
            if (threads == 0) return;

            // Create shared data with thread-local initializers and finalizers
            Data<TLocal> data = new(threads, fromInclusive, toExclusive, action, init, initValue, @finally, parallelOptions.CancellationToken);

            // Queue work items to the thread pool for all threads except the current one
            InitProcessor<TLocal> worker = new(data);
            QueueWorkers(data, worker, threads - 1);

            // Execute work on the current thread
            worker.Execute();

            // If there are still active threads, wait for them to complete
            if (data.ActiveThreads > 0)
            {
                WaitForWorkers(data);
            }

            // Rethrow the first captured worker exception, if any, on the calling thread
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
                int i = _data.Index.GetNext();
                if (i >= _data.ToExclusive || _data.CancellationToken.IsCancellationRequested || _data.IsFaulted)
                {
                    return;
                }

                value = _data.Init();
                initSucceeded = true;
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

    private static partial BackgroundWork BackgroundForCore(int fromInclusive, int toExclusive,
        ParallelOptions options, Action<int> action, Action? completed)
        => new(fromInclusive, toExclusive, options, action, completed);

    public sealed partial class BackgroundWork : IThreadPoolWorkItem
    {
        private const int JoinerFinalizes = 1;
        private const int WorkerFinalizes = 2;
        private ClaimCounter _next;
        private readonly int _to;
        private Action<int>? _action;
        private Action? _completedAction;
        private readonly CancellationToken _token;
        private readonly object _completion = new();
        private int _active = -1;
        private int _finalizer;
        private int _joiner;
        private int _joinerWaiting;
        private bool _abandoned;
        private bool _complete;
        private bool _joined;
        private ExceptionDispatchInfo? _exception;

        private readonly WorkerScope? _scope;
        private readonly WorkerScope.WorkQueue? _queue;
        private readonly BackgroundWork? _dependency;
        private readonly int _workers;
        private BackgroundWork? _continuation;

        internal BackgroundWork(int from, int to, ParallelOptions options, Action<int> action, Action? completed,
            BackgroundWork? dependency = null)
        {
            _scope = dependency?._scope ?? WorkerScope.Current;
            _queue = _scope is null ? null : new();
            options.CancellationToken.ThrowIfCancellationRequested();
            int limit = options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : Environment.ProcessorCount;
            if (_scope is not null) limit = Math.Min(limit, _scope.Concurrency);
            // The reserved caller slot must not reduce the number of iterations that can start in the background.
            _workers = from < to ? (int)Math.Min((long)to - from + 1, limit) : 1;
            _dependency = dependency;
            _next.Value = from;
            _to = to;
            _action = action;
            _completedAction = completed;
            _token = options.CancellationToken;
            if (dependency is null) QueueWorkers();
        }

        // A serial continuation must be able to run before its owner joins.
        private void QueueWorkers() => QueueWorker(_dependency is null ? _workers - 1 : Math.Max(1, _workers - 1));

        private void QueueWorker(int count = 1, bool resuming = false)
        {
            if (_scope is not null) _scope.Enqueue(_queue!, this, resuming, count);
            else
            {
                for (int i = 0; i < count; i++)
                    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }
        }

        private void Run()
        {
            if (_scope is not null) _scope.Run(this);
            else Execute();
        }

        private partial BackgroundWork ContinueWithCore(int fromInclusive, int toExclusive, ParallelOptions options,
            Action<int> action, Action? completed)
        {
            lock (_completion)
            {
                ObjectDisposedException.ThrowIf(_abandoned, this);
                if (_continuation is not null) throw new InvalidOperationException("A continuation is already attached.");
                BackgroundWork next = new(fromInclusive, toExclusive, options, action, completed, this);
                _continuation = next;
                if (_complete && _exception is null && !_token.IsCancellationRequested) next.QueueWorkers();
                return next;
            }
        }

        public partial bool TryHelp() => !_joined && _scope is not null && _scope.TryExecute(_queue!);

        public partial void WaitForCompletion()
        {
            Join();
            _exception?.Throw();
            _token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_abandoned, this);
        }

        public partial void Dispose()
        {
            if (_joined) return;
            if (!Volatile.Read(ref _complete)) Volatile.Write(ref _abandoned, true);
            _dependency?.Dispose();
            Join();
        }

        private void Join()
        {
            if (_joined) return;
            if (_dependency is not null)
            {
                try
                {
                    _dependency.WaitForCompletion();
                }
                catch (Exception ex)
                {
                    Capture(ex);
                }
            }
            if (_scope is not null)
            {
                JoinScoped();
                _joined = true;
                return;
            }
            bool ownsFinalizer = Interlocked.CompareExchange(ref _finalizer, JoinerFinalizes, 0) == 0;
            Run();
            SpinWait spinner = default;
            while (!Ready(ownsFinalizer) && !spinner.NextSpinWillYield) spinner.SpinOnce();
            if (ownsFinalizer && !Ready(ownsFinalizer))
            {
                // Do not wake a sleeping joiner just to run the serial tail. If the last worker
                // already yielded finalization to us, reclaim it after releasing the reservation.
                // A full fence prevents both sides from missing the other's state change.
                Interlocked.Exchange(ref _finalizer, 0);
                ownsFinalizer = Volatile.Read(ref _active) == 0
                    && Interlocked.CompareExchange(ref _finalizer, JoinerFinalizes, 0) == 0;
            }
            if (!Ready(ownsFinalizer)) WaitSlow(ownsFinalizer);
            if (ownsFinalizer) Finish();
            _joined = true;
        }

        private void JoinScoped()
        {
            Volatile.Write(ref _joiner, Environment.CurrentManagedThreadId);
            while (!Volatile.Read(ref _complete))
            {
                // The caller slot is reserved, so claim queued slots only after it stops: taking one first
                // retires a requested runner and loses a thread for the whole range.
                Run();
                while (_scope!.TryExecute(_queue!)) { }
                // Short tails finish without the kernel wake-up a monitor wait costs.
                SpinWait spinner = default;
                while (!Volatile.Read(ref _complete) && !spinner.NextSpinWillYield) spinner.SpinOnce();
                lock (_completion)
                {
                    // Executors change their slot count without the lock; announce the wait with a full fence
                    // before rechecking, so an executor either sees the waiter or leaves a state that ends it.
                    Interlocked.Exchange(ref _joinerWaiting, 1);
                    int active = Volatile.Read(ref _active);
                    if (!_complete && (active == 0 || active >= _workers || (active > 0 &&
                        (Volatile.Read(ref _next.Value) >= _to || _abandoned || _token.IsCancellationRequested || _exception is not null))))
                        Monitor.Wait(_completion);
                    Volatile.Write(ref _joinerWaiting, 0);
                }
            }
        }

        private bool Ready(bool ownsFinalizer) => ownsFinalizer
            ? Volatile.Read(ref _active) == 0 : Volatile.Read(ref _complete);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void WaitSlow(bool ownsFinalizer)
        {
            lock (_completion)
                while (!Ready(ownsFinalizer)) Monitor.Wait(_completion);
        }

        private void Capture(Exception ex) => Interlocked.CompareExchange(ref _exception, ExceptionDispatchInfo.Capture(ex), null);

        private bool TryRegister()
        {
            // -1 reserves the first executor; zero permanently closes registration. Queued callbacks
            // arriving after completion cannot touch buffers released by the joiner.
            int active = Volatile.Read(ref _active);
            while (active != 0 && (_scope is null && _dependency is null || active < _workers))
            {
                int observed = Interlocked.CompareExchange(ref _active, active < 0 ? 1 : active + 1, active);
                if (observed == active) return true;
                active = observed;
            }
            return false;
        }

        void IThreadPoolWorkItem.Execute() => Execute();

        private void Execute()
        {
            if (_scope is not null)
            {
                ExecuteChunk();
                return;
            }
            if (!TryRegister()) return;
            try
            {
                long i = Interlocked.Increment(ref _next.Value) - 1;
                while (i < _to && !Volatile.Read(ref _abandoned)
                    && !_token.IsCancellationRequested && Volatile.Read(ref _exception) is null)
                {
                    _action!((int)i);
                    i = Interlocked.Increment(ref _next.Value) - 1;
                }
            }
            catch (Exception ex)
            {
                Capture(ex);
            }
            finally
            {
                if (Interlocked.Decrement(ref _active) == 0)
                {
                    if (Interlocked.CompareExchange(ref _finalizer, WorkerFinalizes, 0) == 0) Finish();
                    else
                    {
                        lock (_completion) Monitor.PulseAll(_completion);
                    }
                }
            }
        }

        private void ExecuteChunk()
        {
            // Executors leaving together convoy on a lock, so slots are claimed and released lock-free.
            if (!TryRegister()) return;
            // The joiner helps only this operation, so yielding its slot would idle the caller.
            bool joining = Environment.CurrentManagedThreadId == Volatile.Read(ref _joiner);
            try
            {
                // Yield between batches so newly ready storage or hashing work can use the same executors.
                // Without ready work, keep the slot: requeueing only adds lock traffic that convoys the executors.
                for (int count = 0; !Volatile.Read(ref _abandoned)
                    && !_token.IsCancellationRequested && Volatile.Read(ref _exception) is null; count++)
                {
                    if (count == 16)
                    {
                        if (!joining && _scope!.HasOtherReadyWork(_queue!)) break;
                        count = 0;
                    }
                    long i = Interlocked.Increment(ref _next.Value) - 1;
                    if (i >= _to) break;
                    _action!((int)i);
                }
            }
            catch (Exception ex)
            {
                Capture(ex);
            }
            finally
            {
                bool remaining = Volatile.Read(ref _next.Value) < _to && !Volatile.Read(ref _abandoned)
                    && !_token.IsCancellationRequested && Volatile.Read(ref _exception) is null;
                // The last executor reopens registration while work remains; otherwise it closes it and finishes.
                int active = Volatile.Read(ref _active);
                int released;
                while (true)
                {
                    released = active == 1 && remaining ? -1 : active - 1;
                    int observed = Interlocked.CompareExchange(ref _active, released, active);
                    if (observed == active) break;
                    active = observed;
                }
                if (released == 0) Finish();
                else
                {
                    if (Volatile.Read(ref _joinerWaiting) != 0)
                    {
                        lock (_completion) Monitor.PulseAll(_completion);
                    }
                    if (remaining) QueueWorker(resuming: true);
                }
            }
        }

        private void Finish()
        {
            BackgroundWork? continuation;
            try
            {
                if (_exception is null && !_token.IsCancellationRequested && !Volatile.Read(ref _abandoned))
                    _completedAction?.Invoke();
            }
            catch (Exception ex)
            {
                Capture(ex);
            }
            finally
            {
                lock (_completion)
                {
                    _action = null;
                    _completedAction = null;
                    Volatile.Write(ref _complete, true);
                    continuation = _continuation;
                    Monitor.PulseAll(_completion);
                }
            }
            if (_exception is null && !_token.IsCancellationRequested && !Volatile.Read(ref _abandoned))
                continuation?.RunContinuation();
        }

        // Apple silicon keeps coherence in 128-byte lines: the claim counter, updated by every iteration, must
        // not share one with the fields every iteration reads.
        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct ClaimCounter
        {
            [FieldOffset(128)] public long Value;
        }

        private void RunContinuation()
        {
            // Reuse the finishing worker, reserving one slot for the eventual joiner.
            QueueWorker(_workers - 2);
            Run();
        }
    }
}
