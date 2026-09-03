// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

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
/// <typeparam name="TWorkerState">
/// What one thread runs with: built once per thread by the caller's
/// <see cref="IJobStateProvider"/> and handed back to the runner with every job that
/// thread takes. Whatever the threads share, they share through it. Disposing the executor disposes
/// every one of them, since it is what built all but the first.
/// </typeparam>
/// <typeparam name="TJob">
/// One job's inputs and outputs, held by value in the node carrying it: the thread that queues a job
/// fills the inputs, the thread that runs it writes the outputs back through <see cref="Node.Job"/>.
/// </typeparam>
internal sealed class WorkStealingExecutor<TWorkerState, TJob> : IDisposable
    where TWorkerState : class, WorkStealingExecutor<TWorkerState, TJob>.IJobWorkerState
    where TJob : struct
{
    /// <summary>Runs the jobs this executor hands out, on whichever thread took them.</summary>
    internal interface IJobRunner
    {
        /// <summary>Runs one job, reading and writing it in place.</summary>
        /// <param name="state">The state of the thread running it.</param>
        /// <param name="queue">Where that thread queues work of its own, and waits for it.</param>
        void Execute(ref TJob job, TWorkerState state, JobQueue queue);
    }

    /// <summary>Builds the state one thread runs with.</summary>
    /// <remarks>
    /// The state is given nothing of the executor's: where to queue work arrives with each job, so what
    /// a thread holds is the caller's business alone. What it is built holding, though, the executor
    /// disposes — see <see cref="Dispose"/>.
    /// </remarks>
    internal interface IJobStateProvider
    {
        TWorkerState Create();
    }

    /// <summary>What one thread of a run holds, which the executor tells when the run is through.</summary>
    /// <remarks>
    /// <see cref="Complete"/> comes only where the run finished as the caller wanted; a run abandoned
    /// part-way is disposed without it. Both run on the calling thread, once every other has gone quiet.
    /// </remarks>
    internal interface IJobWorkerState : IDisposable
    {
        /// <summary>The run is through: make of what this thread holds whatever the caller means to keep.</summary>
        void Complete();
    }

    /// <summary>Jobs one queue holds before <see cref="JobQueue.TryQueue"/> starts refusing them.</summary>
    /// <remarks>
    /// A queue only fills where the stealing has fallen far behind: a caller queues a handful per
    /// frame of its own descent and waits them out before the frame above it moves on.
    /// </remarks>
    private const int QueueCapacity = 64;

    /// <summary>How many jobs a thread may take on while waiting, one nested inside the next.</summary>
    private const int MaxHelpDepth = 8;

    private readonly IJobRunner _runner;
    private readonly JobQueue[] _queues;
    private bool _done;

    /// <param name="threadCount">Threads to run across, the calling one included; 1 leaves every job to the caller.</param>
    /// <param name="main">The calling thread's state, which exists already — it is what builds this.</param>
    /// <param name="provider">Builds the state for every other thread; called here, on the calling thread.</param>
    /// <param name="runner">Runs one job against the state of whichever thread took it.</param>
    public WorkStealingExecutor(
        int threadCount, TWorkerState main, IJobStateProvider provider, IJobRunner runner)
    {
        _runner = runner;
        _queues = new JobQueue[threadCount];
        States = new TWorkerState[threadCount];

        States[0] = main;
        for (int index = 0; index < threadCount; index++) _queues[index] = new JobQueue(this, index, threadCount > 1 ? QueueCapacity : 0);
        for (int index = 1; index < threadCount; index++) States[index] = provider.Create();
    }

    /// <summary>Whether there is any thread but the caller's, which is what makes queueing a job worth anything.</summary>
    public bool IsParallel => _queues.Length > 1;

    /// <summary>Each thread's state, the calling thread's first, for the caller's own teardown.</summary>
    public TWorkerState[] States { get; }

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

    /// <summary>
    /// Ends the run as the caller wanted it: the threads exit as they notice, and every one of their
    /// states is told the run is through.
    /// </summary>
    /// <remarks>
    /// Nothing waits for the threads to exit — by the time a caller reaches here its outermost wait is
    /// through, so no state is still being run against. A run abandoned part-way is not completed, only
    /// disposed.
    /// </remarks>
    public void Complete()
    {
        Volatile.Write(ref _done, true);
        foreach (TWorkerState state in States) state?.Complete();
    }

    /// <summary>Ends the run, if it has not been ended already, and disposes every thread's state.</summary>
    /// <remarks>
    /// The calling thread's state included: it was handed over to be run with, and the states are what
    /// the executor has of the caller's.
    /// </remarks>
    public void Dispose()
    {
        Volatile.Write(ref _done, true);
        foreach (TWorkerState state in States) state?.Dispose();
    }

    /// <summary>Runs <paramref name="node"/>'s job here and now, leaving what it produced — or threw — on the node.</summary>
    private void Run(TWorkerState state, JobQueue queue, Node node)
    {
        try
        {
            _runner.Execute(ref node.Job, state, queue);
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
    internal sealed class JobQueue(WorkStealingExecutor<TWorkerState, TJob> executor, int index, int capacity)
        : IThreadPoolWorkItem
    {
        private readonly WorkStealingDeque<Node>? _deque = capacity == 0 ? null : new WorkStealingDeque<Node>(capacity);
        private int _helpDepth;

        /// <summary>Whether a queued job can reach another thread at all; on a single-threaded run it cannot.</summary>
        public bool CanQueue => _deque is not null;

        /// <remarks>Read back rather than held, so that a queue and its state need not be built in one go.</remarks>
        private TWorkerState State => executor.States[index];

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
        /// Three passes, and the order of them matters: everything this thread can still claim is
        /// claimed before anything is waited on, because waiting per node as it goes would leave this
        /// thread idle on one stolen job while the siblings behind it sat unstarted.
        /// <para>
        /// A node this thread loses the claim on is already running on the thread that won it, and that
        /// thread waits in turn only on jobs it queued itself — which are strictly further down the
        /// caller's own recursion — so every wait here ends.
        /// </para>
        /// <para>
        /// Whether a job succeeded is the caller's to read off <see cref="Node.Error"/> as it walks the
        /// handle: a job that threw is not this type's to interpret, and the caller may have results of
        /// its own to unwind before it rethrows.
        /// </para>
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
                if (popped.TryClaim()) executor.Run(State, this, popped);
            }

            // A node a thief took off the queue but has not claimed is still this thread's to run: the
            // claim settles which of the two it falls to. This is the last moment this thread can help.
            for (Node? node = queued; node is not null; node = node.Next)
            {
                if (node.TryClaim()) executor.Run(State, this, node);
            }

            // Whatever the claims above lost is running on the thread that won it, and a node counts as
            // done only once that thread has written its outputs onto it. This is where that is waited
            // out — helping with other threads' queues rather than spinning, while it is.
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

                executor.Run(State, this, stolen);
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
