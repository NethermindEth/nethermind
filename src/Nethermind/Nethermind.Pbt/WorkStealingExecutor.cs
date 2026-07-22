// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>
/// Runs a recursive fork/join across a fixed set of threads, each holding a queue the others steal
/// from: a caller descending its own work hands the parts it thinks worth splitting to
/// <see cref="Lane.TrySpawn"/>, carries on with the rest, and picks the results up at
/// <see cref="Lane.Join"/>.
/// </summary>
/// <remarks>
/// The queues are a hint and nothing more. A job is run by whichever thread claims it, and the lane
/// that spawned it claims whatever is left when it comes to join, so a job that is never stolen — or
/// whose push was refused, or that was popped by a thief that then lost the claim — is run by the
/// spawning thread all the same. Nothing is therefore lost to a queue that is full, empty-looking or
/// racing, which is what lets the queue itself stay lock-free and approximate.
/// <para>
/// <see cref="Complete"/> does not wait for the lanes. A caller reaches it only once its outermost
/// join is through, by which point every job is finished, so a lane the thread pool has yet to
/// schedule can do no more than pop a stale hint and fail to claim it. That is what keeps a run from
/// waiting on the pool to schedule anything at all — and it is also why a lane's queue must own its
/// array outright: a pooled one would still be scanned after the run that rented it had ended.
/// </para>
/// <para>
/// State comes in two kinds and the split is the point: <typeparamref name="TState"/> is the one
/// instance every thread shares, <typeparamref name="TWorkerState"/> is a thread's own, built once
/// per lane and handed back to every job that lane runs.
/// </para>
/// </remarks>
/// <typeparam name="TJob">
/// One job's inputs and outputs, held by value in the node carrying it: the spawning lane fills the
/// inputs, the running thread writes the outputs back through <see cref="Node.Job"/>.
/// </typeparam>
internal sealed class WorkStealingExecutor<TState, TWorkerState, TJob>
    where TState : class
    where TWorkerState : class
    where TJob : struct
{
    /// <summary>Runs one job, reading and writing it in place.</summary>
    public delegate void JobRunner(TState state, TWorkerState worker, ref TJob job);

    /// <summary>Jobs one lane's queue holds before <see cref="Lane.TrySpawn"/> starts refusing them.</summary>
    /// <remarks>
    /// A queue only fills where the stealing has fallen far behind: a caller spawns a handful per
    /// frame of its own descent and joins them before the frame above it moves on.
    /// </remarks>
    private const int QueueCapacity = 64;

    /// <summary>How many jobs a thread may take on while waiting, one nested inside the next.</summary>
    private const int MaxHelpDepth = 8;

    private readonly TState _state;
    private readonly JobRunner _runner;
    private readonly Lane[] _lanes;
    private bool _done;

    /// <param name="workerCount">Threads to run across, the caller's own included; 1 leaves every job to the caller.</param>
    /// <param name="state">The state every lane shares.</param>
    /// <param name="createWorker">Builds one lane's own state; called once per lane, on this thread.</param>
    /// <param name="runner">Runs one job. Whatever it throws is caught and left on the job's node.</param>
    public WorkStealingExecutor(
        int workerCount, TState state, Func<TState, Lane, TWorkerState> createWorker, JobRunner runner)
    {
        _state = state;
        _runner = runner;
        _lanes = new Lane[workerCount];
        Workers = new TWorkerState[workerCount];

        // The lanes come first: a lane's state is built from the lane, and reads its way back to the
        // state through this executor rather than being handed it, so neither has to be patched in
        // after the other.
        for (int index = 0; index < workerCount; index++) _lanes[index] = new Lane(this, index, workerCount > 1 ? QueueCapacity : 0);
        for (int index = 0; index < workerCount; index++) Workers[index] = createWorker(state, _lanes[index]);
    }

    /// <summary>Whether there is any thread but the caller's, which is what makes a spawn worth anything.</summary>
    public bool IsParallel => _lanes.Length > 1;

    /// <summary>Each lane's own state, the calling thread's first, for the caller's own teardown.</summary>
    public TWorkerState[] Workers { get; }

    /// <summary>The calling thread's lane, which the outermost work descends on.</summary>
    public Lane MainLane => _lanes[0];

    /// <summary>Queues every lane but the caller's onto the thread pool.</summary>
    public void Start()
    {
        for (int index = 1; index < _lanes.Length; index++)
        {
            // no Task: a lane is its own work item, so a run queues no delegate and no continuation
            ThreadPool.UnsafeQueueUserWorkItem(_lanes[index], preferLocal: false);
        }
    }

    /// <summary>Tells the lanes the run is over; they exit as they notice, and nothing waits for them.</summary>
    public void Complete() => Volatile.Write(ref _done, true);

    /// <summary>Runs <paramref name="node"/>'s job on this thread, leaving what it produced — or threw — on the node.</summary>
    private void Run(TWorkerState worker, Node node)
    {
        try
        {
            _runner(_state, worker, ref node.Job);
            node.Complete(error: null);
        }
        catch (Exception exception)
        {
            node.Complete(exception);
        }
    }

    /// <summary>One thread's share of a run: the queue it hands work out through, and the state it runs with.</summary>
    /// <remarks>
    /// Everything but <see cref="WorkStealingDeque{T}.TrySteal"/> is the owning thread's alone. A lane
    /// is also its own thread-pool work item, which is what <see cref="Start"/> queues.
    /// </remarks>
    internal sealed class Lane(WorkStealingExecutor<TState, TWorkerState, TJob> executor, int index, int queueCapacity)
        : IThreadPoolWorkItem
    {
        private readonly WorkStealingDeque<Node>? _queue = queueCapacity == 0 ? null : new WorkStealingDeque<Node>(queueCapacity);
        private int _helpDepth;

        /// <summary>Whether a spawn can reach another thread at all; on a single-lane run it cannot.</summary>
        public bool CanSpawn => _queue is not null;

        /// <summary>Where this lane's queue stands, which a caller takes as a mark before spawning a batch of its own.</summary>
        public long QueueMark => _queue?.Head ?? 0;

        /// <remarks>Read back rather than held, so that a lane and its state need not be built in one go.</remarks>
        private TWorkerState Worker => executor.Workers[index];

        /// <summary>
        /// Offers <paramref name="job"/> to whichever thread reaches it first and returns the node
        /// holding it; <c>null</c> when the queue is full, leaving the work to the caller.
        /// </summary>
        /// <param name="next">The node this lane spawned before it, which this one is chained ahead of.</param>
        public Node? TrySpawn(in TJob job, Node? next)
        {
            Node node = new(job, next);
            return _queue!.TryPushHead(node) ? node : null;
        }

        /// <summary>
        /// Returns once every node <paramref name="spawned"/> chains has been run: taking back what no
        /// thread has started, then helping with what the other lanes have queued while the rest come back.
        /// </summary>
        /// <param name="queueMark">Where <see cref="QueueMark"/> stood before the caller spawned any of them.</param>
        /// <remarks>
        /// Whether a job succeeded is the caller's to read off <see cref="Node.Error"/>: a job that
        /// threw is not this type's to interpret, and the caller may have results of its own to unwind
        /// before it rethrows.
        /// </remarks>
        public void Join(Node spawned, long queueMark)
        {
            // Take back what no thread has started yet, which the queue hands over newest first — the
            // order the caller pushed them in, and the one whose work is likeliest still cached.
            while (QueueMark > queueMark)
            {
                Node? queued = _queue!.TryPopHead();
                if (queued is null) break;
                if (queued.TryClaim()) executor.Run(Worker, queued);
            }

            // A node a thief took off the queue but has not claimed is still the caller's to run: the
            // claim settles which of the two threads it falls to, and the loser leaves it alone.
            for (Node? node = spawned; node is not null; node = node.Next)
            {
                if (node.TryClaim()) executor.Run(Worker, node);
            }

            for (Node? node = spawned; node is not null; node = node.Next) WaitFor(node);
        }

        /// <summary>Runs whatever the other lanes hand out until the run is through.</summary>
        /// <remarks>
        /// It spins rather than parking: a run lasts a few milliseconds, and nothing else is waiting on
        /// its result, so the wake-up latency of a park would cost more than the spin does. The spin
        /// never sleeps for the same reason, and yields so that a machine with fewer cores than lanes
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
        /// other lanes have queued in the meantime rather than spinning through it.
        /// </summary>
        /// <remarks>
        /// Helping is what keeps a caller that handed all of its work out from idling until it comes
        /// back. It is bounded by <see cref="MaxHelpDepth"/>: each helped job runs and joins on this
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

        /// <summary>Takes one job off another lane's queue and runs it; <c>false</c> when nothing was there to take.</summary>
        private bool TryRunStolen()
        {
            Lane[] lanes = executor._lanes;
            for (int offset = 1; offset < lanes.Length; offset++)
            {
                Node? stolen = lanes[(index + offset) % lanes.Length]._queue!.TrySteal();
                if (stolen is null || !stolen.TryClaim()) continue;

                executor.Run(Worker, stolen);
                return true;
            }

            return false;
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

        /// <summary>The job itself: the caller's inputs going in, the runner's outputs coming back.</summary>
        public ref TJob Job => ref _job;

        /// <summary>The node the same caller spawned before this one, which it walks to join them all.</summary>
        public Node? Next => next;

        /// <summary>What the runner threw, for the caller to make of what it will.</summary>
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
