// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

public static partial class TrieUpdater
{
    /// <summary>
    /// What every thread folding one batch shares — the store, the batch, the encoding to write — and
    /// the <see cref="Worker"/>s that do the folding.
    /// </summary>
    /// <remarks>
    /// The batch is held as its arrays rather than as spans: a bucket is a range of entry indices, which
    /// is what lets one thread hand it to another. The ranges a frame hands out are the buckets it has
    /// just partitioned, so they are disjoint, and the keys the subtree under one writes are all below
    /// that bucket's own — two threads therefore never touch the same entry, the same node key or the
    /// same stem.
    /// </remarks>
    private sealed class Updater
    {
        /// <summary>Below this many stems a batch folds on the calling thread, whatever the concurrency says.</summary>
        /// <remarks>
        /// Measured: a fold of a few hundred stems is a few tens of microseconds, which the hand-offs and
        /// the buffered writes cost more than the threads save; from about a thousand it pays, and by ten
        /// thousand it is worth several times over.
        /// </remarks>
        private const int MinEntriesToParallelize = 1024;

        /// <summary>The smallest bucket worth handing to another thread, whatever the batch's size says.</summary>
        private const int HardMinimumStems = 8;

        /// <summary>
        /// How many jobs the threshold aims to leave per worker: enough for the stealing to even out an
        /// unbalanced batch, few enough that the hand-offs stay a rounding error.
        /// </summary>
        private const int TargetJobsPerWorker = 16;

        private readonly Worker[] _workers;
        private bool _done;

        public Updater(
            IPbtStore store, IRefCountingMemoryProvider memoryProvider, PbtGroupFormat writeFormat,
            PbtWriteBatch changes, int concurrency)
        {
            Store = store;
            MemoryProvider = memoryProvider;
            WriteFormat = writeFormat;
            Entries = changes.EntriesArray;
            Buckets = changes.BucketsArray;

            int workerCount = ResolveWorkerCount(concurrency, changes.Count);
            // The threshold splits the batch into about TargetJobsPerWorker jobs per worker, so that the
            // queues hold enough to steal from without the descent stopping to spawn every other frame.
            MinSpawnEntries = workerCount == 1
                ? int.MaxValue
                : Math.Max(HardMinimumStems, changes.Count / workerCount / TargetJobsPerWorker);

            _workers = new Worker[workerCount];
            for (int index = 0; index < workerCount; index++) _workers[index] = new Worker(this, index);
        }

        public IPbtStore Store { get; }

        public IRefCountingMemoryProvider MemoryProvider { get; }

        public PbtGroupFormat WriteFormat { get; }

        /// <summary>The batch's entries, which the descent permutes in place — each job over its own range of them.</summary>
        public PbtWriteBatch.StemEntry[] Entries { get; }

        /// <inheritdoc cref="PbtWriteBatch.Buckets"/>
        public int[]? Buckets { get; }

        /// <summary>
        /// The smallest bucket a frame hands to another thread; <see cref="int.MaxValue"/> when the fold
        /// runs on the calling thread alone, which is what takes the spawn out of the descent entirely.
        /// </summary>
        public int MinSpawnEntries { get; }

        public bool IsParallel => _workers.Length > 1;

        /// <summary>Whether the fold has finished, which is what the stealing workers exit on.</summary>
        public bool IsDone => Volatile.Read(ref _done);

        public int WorkerCount => _workers.Length;

        public Worker WorkerAt(int index) => _workers[index];

        /// <summary>Folds <paramref name="changes"/> into the tree at <paramref name="currentRoot"/> and returns the new root.</summary>
        /// <remarks>
        /// The calling thread is worker 0 and runs the root frame; the rest only ever run what it and its
        /// descendants hand out. It returns without waiting for them: once the root frame's join is
        /// through, every job is finished, so a worker still to be scheduled can find nothing left to
        /// claim and exits on <see cref="IsDone"/> — which is what keeps a fold from waiting on the thread
        /// pool to schedule anything at all.
        /// </remarks>
        public ValueHash256 Run(in ValueHash256 currentRoot, PbtWriteBatch changes, out PbtSubtreeStats delta)
        {
            StartWorkers();

            bool folded = false;
            try
            {
                ValueHash256 root = _workers[0].Run(currentRoot, changes, out delta);
                folded = true;
                return root;
            }
            finally
            {
                Volatile.Write(ref _done, true);
                FlushWrites(folded);
            }
        }

        /// <remarks>
        /// Bounded by the work as well as by the machine: a batch that cannot be cut into
        /// <see cref="TargetJobsPerWorker"/> jobs of <see cref="HardMinimumStems"/> stems for each worker
        /// asked for would leave the rest of them with nothing to steal, spinning through the fold.
        /// </remarks>
        private static int ResolveWorkerCount(int concurrency, int stems)
        {
            if (stems < MinEntriesToParallelize) return 1;

            int requested = concurrency <= 0 ? Environment.ProcessorCount : concurrency;
            int affordable = stems / (HardMinimumStems * TargetJobsPerWorker);
            return Math.Clamp(Math.Min(requested, affordable), 1, Environment.ProcessorCount);
        }

        private void StartWorkers()
        {
            for (int index = 1; index < _workers.Length; index++)
            {
                Task.Run(_workers[index].StealLoop);
            }
        }

        /// <summary>
        /// Hands the store what the workers buffered, on the calling thread and in each worker's own
        /// order, or drops it where the fold threw and the writes are not to be kept.
        /// </summary>
        /// <remarks>
        /// Every worker is quiescent by now: the root frame's join settled the last job before
        /// <see cref="Run"/> reached here, and a job's completion publishes its writes to whoever waits
        /// on it.
        /// </remarks>
        private void FlushWrites(bool commit)
        {
            foreach (Worker worker in _workers) worker.FlushWrites(commit);
        }
    }

    private sealed partial class Worker
    {
        /// <summary>
        /// Jobs one worker's queue holds before a frame folds its buckets itself instead. A frame spawns
        /// at most fifteen, and a frame under one is joined before the frame above it moves on, so this
        /// only fills where the stealing has fallen far behind.
        /// </summary>
        private const int QueueCapacity = 64;

        /// <summary>The jobs this worker hands out, which any other may take; <c>null</c> for a fold on the calling thread alone.</summary>
        private readonly WorkStealingDeque<Job>? _queue = updater.IsParallel ? new WorkStealingDeque<Job>(QueueCapacity) : null;

        /// <summary>
        /// The store writes this worker made, replayed by the calling thread once the fold is through;
        /// <c>null</c> for a fold on the calling thread alone, which writes straight through.
        /// </summary>
        /// <remarks>
        /// Buffering is what keeps <see cref="IPbtStore"/> a single-threaded interface: only the reads
        /// run concurrently, and nothing writes the store while they do. The two lists need no order
        /// between them — a node key and a stem name different things — and the writes of two workers
        /// need none either, their key ranges being disjoint. What is buffered is the value's own array
        /// rather than the buffer it was folded in, which goes back to the pool at the write: holding
        /// a fold's worth of rentals to the end of it would leave every worker renting fresh ones.
        /// </remarks>
        private readonly List<(TrieNodeKey Key, byte[]? Node)>? _nodeWrites = updater.IsParallel ? [] : null;

        /// <inheritdoc cref="_nodeWrites"/>
        private readonly List<(Stem Stem, byte[]? Blob)>? _blobWrites = updater.IsParallel ? [] : null;

        /// <summary>Where this worker's queue stands, which a frame takes as a mark before spawning.</summary>
        private long QueueMark => _queue?.Head ?? 0;

        /// <summary>
        /// Whether this frame may hand any of its buckets out at all: the fold is a parallel one, and the
        /// frame branches. A frame whose entries all fall in one bucket would be handing its whole range
        /// over and then waiting on it, which buys nothing.
        /// </summary>
        private bool MaySpawn(uint touchedBitmask) => _queue is not null && !BitOperations.IsPow2(touchedBitmask);

        /// <summary>
        /// Queues <paramref name="bucket"/> as a job for whichever thread reaches it first, and returns
        /// it; <c>null</c> when the queue is full, leaving the frame to fold the bucket itself.
        /// </summary>
        /// <param name="next">The job this frame spawned before it, which this one is chained ahead of.</param>
        private Job? TrySpawn(
            int slot, in TrieNodeKey childKey, Span<PbtWriteBatch.StemEntry> bucket, in Occupant occupant,
            scoped BucketPlan childPlan, Job? next)
        {
            ReadOnlySpan<int> precalculated = childPlan.Precalculated;
            Job job = new(
                slot, childKey, IndexOf(updater.Entries, bucket), bucket.Length,
                precalculated.IsEmpty ? 0 : IndexOf(updater.Buckets!, precalculated), precalculated.Length,
                childPlan.BranchDepth, occupant, next);

            return _queue!.TryPushHead(job) ? job : null;
        }

        /// <summary>
        /// Waits out the jobs <paramref name="spawned"/> chains — running what nothing else has started,
        /// newest first — and settles each one's result into the frame's boundary.
        /// </summary>
        /// <param name="queueMark">Where this worker's queue stood before the frame spawned anything.</param>
        private void Join(
            Job spawned, long queueMark, Span<NodeResult> results, ref BoundaryScan scan, ref uint storedChildBitmask)
        {
            // Take back what no thread has started yet, which the queue hands over newest first — the
            // order this frame pushed them in, and the one whose entries are likeliest still cached.
            while (QueueMark > queueMark)
            {
                Job? queued = _queue!.TryPopHead();
                if (queued is null) break;
                if (queued.TryClaim()) Execute(queued);
            }

            // A job a thief took off the queue but has not claimed is still this frame's to run: the claim
            // settles which of the two threads it falls to, and the loser leaves it alone.
            for (Job? job = spawned; job is not null; job = job.Next)
            {
                if (job.TryClaim()) Execute(job);
            }

            Exception? error = null;
            for (Job? job = spawned; job is not null; job = job.Next)
            {
                job.Wait();
                if (job.Error is not null)
                {
                    error ??= job.Error;
                    continue;
                }

                if (error is not null)
                {
                    // a sibling threw, so this frame is being abandoned: release what this one folded
                    job.Result.Dispose();
                    continue;
                }

                results[job.Slot] = job.Result;
                scan.Add(job.Slot, job.Result, job.Changed, job.Delta);
                if (job.StoredChild) storedChildBitmask |= 1u << job.Slot;
            }

            if (error is not null) ExceptionDispatchInfo.Throw(error);
        }

        /// <summary>Folds one queued bucket, whichever thread got to it.</summary>
        private void Execute(Job job)
        {
            try
            {
                BucketPlan plan = new(
                    job.BucketLength == 0 ? default : updater.Buckets!.AsSpan(job.BucketStart, job.BucketLength),
                    job.BranchDepth);
                ApplyKeyedChild(
                    job.Key, updater.Entries.AsSpan(job.EntryStart, job.EntryCount), job.Occupant, plan,
                    out NodeResult result, out bool changed, out PbtSubtreeStats delta, out bool storedChild);
                job.Settle(result, changed, delta, storedChild);
            }
            catch (Exception exception)
            {
                job.Fail(exception);
            }
        }

        /// <summary>
        /// Runs whatever the other workers hand out until the fold is through. One of these per worker
        /// past the calling thread's own.
        /// </summary>
        /// <remarks>
        /// It spins rather than parking: a fold lasts a few milliseconds, and nothing else is waiting on
        /// its result, so the wake-up latency of a park would cost more than the spin does. The spin
        /// never sleeps for the same reason, and yields so that a machine with fewer cores than workers
        /// still makes progress.
        /// </remarks>
        public void StealLoop()
        {
            SpinWait spin = default;
            while (!updater.IsDone)
            {
                if (TryRunStolen())
                {
                    spin = default;
                    continue;
                }

                spin.SpinOnce(sleep1Threshold: -1);
            }
        }

        /// <summary>Takes one job off another worker's queue and runs it; <c>false</c> when nothing was there to take.</summary>
        private bool TryRunStolen()
        {
            int workerCount = updater.WorkerCount;
            for (int offset = 1; offset < workerCount; offset++)
            {
                Job? stolen = updater.WorkerAt((index + offset) % workerCount)._queue!.TrySteal();
                if (stolen is null || !stolen.TryClaim()) continue;

                Execute(stolen);
                return true;
            }

            return false;
        }

        /// <summary>
        /// <inheritdoc cref="IPbtStore.SetTrieNode" path="/summary"/> Takes <paramref name="node"/>'s
        /// lease, copying the value out of it as the store would.
        /// </summary>
        /// <remarks>Buffered where the fold is a parallel one — see <see cref="_nodeWrites"/>.</remarks>
        private void SetTrieNode(in TrieNodeKey key, RefCountingMemory? node)
        {
            byte[]? value = node?.ToArrayAndRelease();
            if (_nodeWrites is null) updater.Store.SetTrieNode(key, value);
            else _nodeWrites.Add((key, value));
        }

        /// <summary><inheritdoc cref="SetTrieNode" path="/summary"/></summary>
        /// <remarks><inheritdoc cref="SetTrieNode" path="/remarks"/></remarks>
        private void SetLeafBlob(in Stem stem, RefCountingMemory? blob)
        {
            byte[]? value = blob?.ToArrayAndRelease();
            if (_blobWrites is null) updater.Store.SetLeafBlob(stem, value);
            else _blobWrites.Add((stem, value));
        }

        /// <inheritdoc cref="Updater.FlushWrites"/>
        public void FlushWrites(bool commit)
        {
            if (_nodeWrites is null) return;

            if (commit)
            {
                foreach ((TrieNodeKey key, byte[]? node) in _nodeWrites) updater.Store.SetTrieNode(key, node);
                foreach ((Stem stem, byte[]? blob) in _blobWrites!) updater.Store.SetLeafBlob(stem, blob);
            }

            _nodeWrites.Clear();
            _blobWrites!.Clear();
        }

        /// <summary>Where <paramref name="span"/> starts in <paramref name="array"/>, which it must be a range of.</summary>
        private static int IndexOf<T>(T[] array, ReadOnlySpan<T> span)
        {
            ref T start = ref MemoryMarshal.GetArrayDataReference(array);
            ref T at = ref Unsafe.AsRef(in MemoryMarshal.GetReference(span));
            nint index = Unsafe.ByteOffset(ref start, ref at) / Unsafe.SizeOf<T>();
            Debug.Assert((nuint)index + (nuint)span.Length <= (nuint)array.Length, "the span must be a range of the array");
            return (int)index;
        }

        /// <summary>
        /// One bucket of one frame, waiting for a thread to fold it: the range of entries, where they go,
        /// and what the fold made of them.
        /// </summary>
        /// <remarks>
        /// The state is what makes the queue no more than a hint: a job is run by whichever thread claims
        /// it, and the frame that spawned it claims whatever is left when it comes to join, so a job that
        /// is never stolen — or whose queue slot was refused, popped or lost to a race — is folded by the
        /// spawning thread all the same. Jobs are not pooled: a recycled one could be claimed a second
        /// time by a thread still holding the stale reference the queue handed it.
        /// </remarks>
        private sealed class Job(
            int slot, TrieNodeKey key, int entryStart, int entryCount, int bucketStart, int bucketLength,
            int branchDepth, Occupant occupant, Job? next)
        {
            private const int Pending = 0;
            private const int Claimed = 1;
            private const int Done = 2;

            private int _state;

            /// <summary>The job the same frame spawned before this one; the frame walks the chain to join them.</summary>
            public Job? Next => next;

            /// <summary>The boundary slot of the parent frame this job's result settles into.</summary>
            public int Slot => slot;

            public TrieNodeKey Key => key;

            public int EntryStart => entryStart;

            public int EntryCount => entryCount;

            public int BucketStart => bucketStart;

            public int BucketLength => bucketLength;

            public int BranchDepth => branchDepth;

            /// <summary>The node the parent's boundary slot holds, borrowed from the encoding the parent frame is reading.</summary>
            public Occupant Occupant => occupant;

            public NodeResult Result { get; private set; }

            public bool Changed { get; private set; }

            public PbtSubtreeStats Delta { get; private set; }

            /// <inheritdoc cref="ApplyKeyedChild" path="/param[@name='storedChild']"/>
            public bool StoredChild { get; private set; }

            /// <summary>What the fold threw, to be rethrown by the frame that spawned this job.</summary>
            public Exception? Error { get; private set; }

            /// <summary>Takes the job for this thread to run; <c>false</c> when another thread has it.</summary>
            public bool TryClaim() => Interlocked.CompareExchange(ref _state, Claimed, Pending) == Pending;

            public void Settle(in NodeResult result, bool changed, in PbtSubtreeStats delta, bool storedChild)
            {
                Result = result;
                Changed = changed;
                Delta = delta;
                StoredChild = storedChild;
                Volatile.Write(ref _state, Done);
            }

            public void Fail(Exception exception)
            {
                Error = exception;
                Volatile.Write(ref _state, Done);
            }

            /// <summary>Waits for the thread that claimed this job to finish it.</summary>
            public void Wait()
            {
                SpinWait spin = default;
                while (Volatile.Read(ref _state) != Done) spin.SpinOnce(sleep1Threshold: -1);
            }
        }
    }
}
