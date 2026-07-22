// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>
/// A thread's own state, which runs the jobs that thread takes: the executor hands a job back to the
/// state rather than to a callback of its own, so that what runs a job and what it runs with are one
/// thing.
/// </summary>
internal interface IJobRunner<TJob> where TJob : struct
{
    /// <summary>Runs one job, reading and writing it in place.</summary>
    void Execute(ref TJob job);
}

/// <summary>Builds the state one thread runs with, around the queue it hands its own work to.</summary>
internal interface IJobStateProvider<TState, TJob>
    where TState : class, IJobRunner<TJob>
    where TJob : struct
{
    /// <param name="queue">Where the state being built queues work for the other threads, and waits for it.</param>
    TState Create(WorkStealingExecutor<TState, TJob>.JobQueue queue);
}

/// <summary>
/// Runs a recursive fork/join across a fixed set of threads, each with a queue the others steal from:
/// a caller descending its own work hands the parts it thinks worth splitting to
/// <see cref="JobQueue.TryQueue"/>, carries on with the rest, and picks the results up once
/// <see cref="JobQueue.Wait"/> has seen them through.
/// </summary>
/// <remarks>
/// The queues are a hint and nothing more. A job is run by whichever thread claims it, and the thread
/// that queued it claims whatever is left when it comes to wait — so a job that is never stolen, or
/// that a thief popped and then lost the claim to, is run by the thread that queued it all the same,
/// and one the queue refuses was never taken off that thread to begin with. Nothing is therefore lost
/// to a queue that is full, empty-looking or racing, which is what lets the queues stay lock-free and
/// approximate.
/// <para>
/// <see cref="Complete"/> does not wait for the threads. A caller reaches it only once its outermost
/// wait is through, by which point every job is finished, so a thread the pool has yet to schedule
/// can do no more than pop a stale hint and fail to claim it. That is what keeps a run from waiting
/// on the pool to schedule anything at all — and it is also why a queue must own its array outright:
/// a pooled one would still be scanned after the run that rented it had ended.
/// </para>
/// </remarks>
/// <typeparam name="TState">
/// What one thread runs with and through: built once per thread by the caller's
/// <see cref="IJobStateProvider{TState, TJob}"/>, and asked to run every job that thread takes.
/// Whatever the threads share, they share through it.
/// </typeparam>
/// <typeparam name="TJob">
/// One job's inputs and outputs, held by value in the node carrying it: the thread that queues a job
/// fills the inputs, the thread that runs it writes the outputs back through <see cref="Node.Job"/>.
/// </typeparam>
internal sealed class WorkStealingExecutor<TState, TJob>
    where TState : class, IJobRunner<TJob>
    where TJob : struct
{
    /// <summary>Jobs one queue holds before <see cref="JobQueue.TryQueue"/> starts refusing them.</summary>
    /// <remarks>
    /// A queue only fills where the stealing has fallen far behind: a caller queues a handful per
    /// frame of its own descent and waits them out before the frame above it moves on.
    /// </remarks>
    private const int QueueCapacity = 64;

    /// <summary>How many jobs a thread may take on while waiting, one nested inside the next.</summary>
    private const int MaxHelpDepth = 8;

    private readonly JobQueue[] _queues;
    private bool _done;

    /// <param name="threadCount">Threads to run across, the calling one included; 1 leaves every job to the caller.</param>
    /// <param name="main">The calling thread's state, which exists already — it is what builds this.</param>
    /// <param name="provider">Builds the state for every other thread; called here, on the calling thread.</param>
    public WorkStealingExecutor(int threadCount, TState main, IJobStateProvider<TState, TJob> provider)
    {
        _queues = new JobQueue[threadCount];
        States = new TState[threadCount];

        // The queues come first: a state is built around the queue it will hand work to, and a queue
        // reads its own state back off this executor rather than holding it, so neither has to be
        // patched into the other afterwards.
        for (int index = 0; index < threadCount; index++) _queues[index] = new JobQueue(this, index, threadCount > 1 ? QueueCapacity : 0);

        States[0] = main;
        for (int index = 1; index < threadCount; index++) States[index] = provider.Create(_queues[index]);
    }

    /// <summary>Whether there is any thread but the caller's, which is what makes queueing a job worth anything.</summary>
    public bool IsParallel => _queues.Length > 1;

    /// <summary>Each thread's state, the calling thread's first, for the caller's own teardown.</summary>
    public TState[] States { get; }

    /// <summary>The calling thread's queue, which its own work is handed out through.</summary>
    public JobQueue MainQueue => _queues[0];

    /// <summary>Puts every thread but the caller's onto the thread pool.</summary>
    public void Start()
    {
        for (int index = 1; index < _queues.Length; index++)
        {
            // no Task: a queue is its own work item, so a run queues no delegate and no continuation
            ThreadPool.UnsafeQueueUserWorkItem(_queues[index], preferLocal: false);
        }
    }

    /// <summary>Tells the threads the run is over; they exit as they notice, and nothing waits for them.</summary>
    public void Complete() => Volatile.Write(ref _done, true);

    /// <summary>Runs <paramref name="node"/>'s job here and now, leaving what it produced — or threw — on the node.</summary>
    private static void Run(TState state, Node node)
    {
        try
        {
            state.Execute(ref node.Job);
            node.Complete(error: null);
        }
        catch (Exception exception)
        {
            node.Complete(exception);
        }
    }

    /// <summary>
    /// Where one thread hands work to the others and waits for it back — and, behind an explicit
    /// interface the caller never sees, the loop by which that thread takes work from the rest.
    /// </summary>
    /// <remarks>Everything but <see cref="WorkStealingDeque{T}.TrySteal"/> is the owning thread's alone.</remarks>
    internal sealed class JobQueue(WorkStealingExecutor<TState, TJob> executor, int index, int capacity)
        : IThreadPoolWorkItem
    {
        private readonly WorkStealingDeque<Node>? _deque = capacity == 0 ? null : new WorkStealingDeque<Node>(capacity);
        private int _helpDepth;

        /// <summary>Whether a queued job can reach another thread at all; on a single-threaded run it cannot.</summary>
        public bool CanQueue => _deque is not null;

        /// <remarks>Read back rather than held, so that a queue and its state need not be built in one go.</remarks>
        private TState State => executor.States[index];

        /// <summary>
        /// Offers <paramref name="job"/> to whichever thread reaches it first, adding it to
        /// <paramref name="handle"/>; <c>false</c> when the queue is full, which leaves the job with the
        /// caller and the handle untouched.
        /// </summary>
        public bool TryQueue(in TJob job, ref Handle handle)
        {
            Node node = new(job, handle.Head);
            if (!_deque!.TryPushHead(node)) return false;

            // Where the queue stood before this batch's first job went on, which is how far Wait may
            // take work back: anything below that mark belongs to an enclosing batch of the caller's.
            if (handle.Head is null) handle.Mark = _deque.Head - 1;
            handle.Head = node;
            return true;
        }

        /// <summary>
        /// Returns once every job <paramref name="handle"/> holds has been run: taking back what no
        /// thread has started, then helping with what the other threads have queued while the rest come
        /// back.
        /// </summary>
        /// <remarks>
        /// Whether a job succeeded is the caller's to read off <see cref="Node.Error"/> as it walks the
        /// handle: a job that threw is not this type's to interpret, and the caller may have results of
        /// its own to unwind before it rethrows.
        /// </remarks>
        public void Wait(in Handle handle)
        {
            Node? queued = handle.Head;
            if (queued is null) return;

            // Take back what no thread has started yet, which the queue hands over newest first — the
            // order the caller queued them in, and the one whose work is likeliest still cached.
            while (_deque!.Head > handle.Mark)
            {
                Node? popped = _deque.TryPopHead();
                if (popped is null) break;
                if (popped.TryClaim()) Run(State, popped);
            }

            // A node a thief took off the queue but has not claimed is still the caller's to run: the
            // claim settles which of the two threads it falls to, and the loser leaves it alone.
            for (Node? node = queued; node is not null; node = node.Next)
            {
                if (node.TryClaim()) Run(State, node);
            }

            for (Node? node = queued; node is not null; node = node.Next) WaitFor(node);
        }

        /// <summary>Runs whatever the other threads hand out until the run is through.</summary>
        /// <remarks>
        /// It spins rather than parking: a run lasts a few milliseconds, and nothing else is waiting on
        /// its result, so the wake-up latency of a park would cost more than the spin does. The spin
        /// never sleeps for the same reason, and yields so that a machine with fewer cores than threads
        /// still makes progress.
        /// </remarks>
        void IThreadPoolWorkItem.Execute()
        {
            SpinWait spin = default;
            while (!Volatile.Read(ref executor._done))
            {
                if (TryRunStolen())
                {
                    spin = default;
                    continue;
                }

                spin.SpinOnce(sleep1Threshold: -1);
            }
        }

        /// <summary>
        /// Waits for whichever thread claimed <paramref name="node"/> to finish it, running what the
        /// other threads have queued in the meantime rather than spinning through it.
        /// </summary>
        /// <remarks>
        /// Helping is what keeps a caller that handed all of its work out from idling until it comes
        /// back. It is bounded by <see cref="MaxHelpDepth"/>: each helped job runs and waits on this
        /// thread's stack, so an unbounded chain of them — a thread that keeps taking on new work every
        /// time it waits — would run the stack down.
        /// </remarks>
        private void WaitFor(Node node)
        {
            SpinWait spin = default;
            while (!node.IsDone)
            {
                if (_helpDepth < MaxHelpDepth)
                {
                    _helpDepth++;
                    bool helped = TryRunStolen();
                    _helpDepth--;
                    if (helped)
                    {
                        spin = default;
                        continue;
                    }
                }

                spin.SpinOnce(sleep1Threshold: -1);
            }
        }

        /// <summary>Takes one job off another thread's queue and runs it; <c>false</c> when there was none to take.</summary>
        private bool TryRunStolen()
        {
            JobQueue[] queues = executor._queues;
            for (int offset = 1; offset < queues.Length; offset++)
            {
                Node? stolen = queues[(index + offset) % queues.Length]._deque!.TrySteal();
                if (stolen is null || !stolen.TryClaim()) continue;

                Run(State, stolen);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// The jobs one caller has queued and not yet waited on, which it walks for their outputs once
    /// <see cref="JobQueue.Wait"/> is through.
    /// </summary>
    /// <remarks>
    /// A caller starts with <c>default</c> and hands the same handle to every
    /// <see cref="JobQueue.TryQueue"/> of that batch; the chaining, and the queue mark the wait needs,
    /// are the queue's to keep rather than the caller's. Handles nest: a job that queues jobs of its
    /// own holds a handle of its own, and waiting on that one takes back only what it queued.
    /// </remarks>
    internal struct Handle
    {
        /// <summary>The most recently queued job, from which the rest chain backwards.</summary>
        internal Node? Head;

        /// <summary>Where the queue stood before the first of them went on.</summary>
        internal long Mark;

        public readonly bool IsEmpty => Head is null;

        public readonly Enumerator GetEnumerator() => new(Head);

        /// <summary>Walks the jobs, most recently queued first.</summary>
        internal struct Enumerator(Node? head)
        {
            private Node? _next = head;

            public Node Current { get; private set; } = null!;

            public bool MoveNext()
            {
                if (_next is null) return false;

                Current = _next;
                _next = _next.Next;
                return true;
            }
        }
    }

    /// <summary>One job waiting for a thread to run it, and the claim that settles which one does.</summary>
    /// <remarks>
    /// Nodes are not pooled: a recycled one could be claimed a second time by a thread still holding
    /// the stale reference a queue handed it.
    /// </remarks>
    internal sealed class Node(TJob job, Node? next)
    {
        private const int Pending = 0;
        private const int Claimed = 1;
        private const int Done = 2;

        private TJob _job = job;
        private int _state;

        /// <summary>The job itself: the caller's inputs going in, the outputs coming back.</summary>
        public ref TJob Job => ref _job;

        /// <summary>The job queued before this one, which the handle chains back through.</summary>
        internal Node? Next => next;

        /// <summary>What running the job threw, for the caller to make of what it will.</summary>
        public Exception? Error { get; private set; }

        /// <summary>Whether the thread that claimed this node has finished it, outputs and all.</summary>
        public bool IsDone => Volatile.Read(ref _state) == Done;

        /// <summary>Takes the job for this thread to run; <c>false</c> when another thread has it.</summary>
        public bool TryClaim() => Interlocked.CompareExchange(ref _state, Claimed, Pending) == Pending;

        internal void Complete(Exception? error)
        {
            Error = error;
            Volatile.Write(ref _state, Done);
        }
    }
}
