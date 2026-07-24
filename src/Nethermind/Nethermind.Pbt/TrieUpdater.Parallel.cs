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
    // The updater as the executor sees it: the state one thread folds with, the queue it hands its
    // buckets to, and the job it is asked to run. The descent itself is in TrieUpdater.cs.
    private sealed partial class Updater<TLayout> :
        WorkStealingExecutor<Updater<TLayout>, Updater<TLayout>.BucketJob>.IJobRunner,
        WorkStealingExecutor<Updater<TLayout>, Updater<TLayout>.BucketJob>.IJobStateProvider,
        WorkStealingExecutor<Updater<TLayout>, Updater<TLayout>.BucketJob>.IJobWorkerState
        where TLayout : IPbtTileLayout
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
        /// How many jobs the threshold aims to leave per thread: enough for the stealing to even out an
        /// unbalanced batch, few enough that the hand-offs stay a rounding error.
        /// </summary>
        private const int TargetJobsPerThread = 16;

        /// <summary>The threads this fold runs across; the calling thread's own updater owns it.</summary>
        private readonly WorkStealingExecutor<Updater<TLayout>, BucketJob>? _executor;

        /// <summary>
        /// The calling thread's updater, which settles what the fold costs — how many threads, how big a
        /// bucket is worth handing over, how much each thread buffers — and builds the threads to run it.
        /// </summary>
        /// <remarks>
        /// The executor is built last, and deliberately: building it is what calls <see cref="Create"/>
        /// for every other thread, so everything one of those copies has to be settled by then.
        /// </remarks>
        public Updater(
            IPbtStore store, IRefCountingMemoryProvider memoryProvider, PbtGroupFormat writeFormat,
            PbtWriteBatch changes, int concurrency)
        {
            _store = store;
            _memoryProvider = memoryProvider;
            _writeFormat = writeFormat;

            // held as arrays rather than as the batch: a bucket is a range of entry indices, which is
            // what lets one thread hand it to another
            _entries = changes.EntriesArray;
            _buckets = changes.BucketsArray;

            int threadCount = ResolveThreadCount(concurrency, changes.Count);

            // The threshold splits the batch into about TargetJobsPerThread jobs per thread, so that the
            // queues hold enough to steal from without the descent stopping to queue at every other
            // frame; a fold on the calling thread alone takes the queueing out of the descent entirely.
            _minQueueEntries = threadCount == 1
                ? int.MaxValue
                : Math.Max(HardMinimumStems, changes.Count / threadCount / TargetJobsPerThread);
            _executor = new WorkStealingExecutor<Updater<TLayout>, BucketJob>(threadCount, this, this, this);
        }

        /// <summary>One more thread's updater, folding the same batch with the same settings.</summary>
        private Updater(Updater<TLayout> main)
        {
            _store = main._store;
            _memoryProvider = main._memoryProvider;
            _writeFormat = main._writeFormat;
            _entries = main._entries;
            _buckets = main._buckets;
            _minQueueEntries = main._minQueueEntries;
        }

        /// <inheritdoc cref="WorkStealingExecutor{TWorkerState, TJob}.IJobStateProvider.Create"/>
        public Updater<TLayout> Create() => new(this);

        /// <inheritdoc cref="WorkStealingExecutor{TWorkerState, TJob}.IJobRunner.Execute"/>
        /// <remarks>One bucket of one frame, folded on whichever thread got to it.</remarks>
        public void Execute(ref BucketJob job, Updater<TLayout> state, WorkStealingExecutor<Updater<TLayout>, BucketJob>.JobQueue queue)
        {
            BucketPlan plan = new(
                job.BucketLength == 0 ? default : state._buckets!.AsSpan(job.BucketStart, job.BucketLength),
                job.BranchDepth);
            state.ApplyKeyedChild(
                job.Key, state._entries.AsSpan(job.EntryStart, job.EntryCount), job.Occupant, plan, new Fanout(queue),
                out NodeResult result, out bool changed, out PbtSubtreeStats delta, out bool storedChild);

            job.Result = result;
            job.Changed = changed;
            job.Delta = delta;
            job.StoredChild = storedChild;
        }

        /// <summary>
        /// Folds <paramref name="changes"/> into the tree at <paramref name="currentRoot"/> and returns
        /// the new root, across every thread this updater built.
        /// </summary>
        /// <remarks>
        /// The calling thread runs the root frame, and the rest only ever run what it and its
        /// descendants hand out. Every write they buffered is replayed here, on the calling thread,
        /// which is what keeps <see cref="IPbtStore"/> a single-threaded interface.
        /// </remarks>
        public ValueHash256 Run(in ValueHash256 currentRoot, PbtWriteBatch changes, out PbtSubtreeStats delta)
        {
            _executor!.Start();

            try
            {
                ValueHash256 root = Descend(currentRoot, changes, new Fanout(_executor.MainQueue), out delta);
                _executor.Complete();
                return root;
            }
            finally
            {
                _executor.Dispose();
            }
        }

        /// <remarks>
        /// Bounded by the work as well as by the machine: a batch that cannot be cut into
        /// <see cref="TargetJobsPerThread"/> jobs of <see cref="HardMinimumStems"/> stems for each thread
        /// asked for would leave the rest of them with nothing to steal, spinning through the fold.
        /// </remarks>
        private static int ResolveThreadCount(int concurrency, int stems)
        {
            if (stems < MinEntriesToParallelize) return 1;

            int requested = concurrency <= 0 ? Environment.ProcessorCount : concurrency;
            int affordable = stems / (HardMinimumStems * TargetJobsPerThread);
            return Math.Clamp(Math.Min(requested, affordable), 1, Environment.ProcessorCount);
        }

        /// <summary>
        /// Whether this frame may hand any of its buckets out at all: the fold is a parallel one, and the
        /// frame branches. A frame whose entries all fall in one bucket would be handing its whole range
        /// over and then waiting on it, which buys nothing.
        /// </summary>
        private static bool MayQueue(SlotBitmask<TLayout> touched, in Fanout fanout) =>
            fanout.CanQueue && !touched.HoldsOne;

        /// <summary>
        /// Queues <paramref name="bucket"/> as a job for whichever thread reaches it first, adding it to
        /// <paramref name="queued"/>; <c>false</c> when the queue is full, leaving the frame to fold the
        /// bucket itself.
        /// </summary>
        private bool TryQueue(
            int slot, in TrieNodeKey childKey, Span<PbtWriteBatch.StemEntry> bucket, in Occupant occupant,
            scoped BucketPlan childPlan, in Fanout fanout, ref QueuedBuckets queued)
        {
            ReadOnlySpan<int> precalculated = childPlan.Precalculated;
            BucketJob job = new()
            {
                Slot = slot,
                Key = childKey,
                EntryStart = IndexOf(_entries, bucket),
                EntryCount = bucket.Length,
                BucketStart = precalculated.IsEmpty ? 0 : IndexOf(_buckets!, precalculated),
                BucketLength = precalculated.Length,
                BranchDepth = childPlan.BranchDepth,
                Occupant = occupant,
            };

            return fanout.TryQueue(in job, ref queued);
        }

        /// <summary>
        /// Sees the buckets <paramref name="queued"/> holds through and settles each one's result into
        /// the frame's boundary — rethrowing on this thread whatever one of them threw on another.
        /// </summary>
        private void Settle(
            in Fanout fanout, ref QueuedBuckets queued, Span<NodeResult> results, ref BoundaryScan scan,
            ref SlotBitmask<TLayout> storedChildren)
        {
            fanout.Wait(in queued);

            Exception? error = null;
            foreach (WorkStealingExecutor<Updater<TLayout>, BucketJob>.Node node in queued.Jobs)
            {
                if (node.Error is not null)
                {
                    error ??= node.Error;
                    continue;
                }

                ref BucketJob job = ref node.Job;
                if (error is not null)
                {
                    // a sibling threw, so this frame is being abandoned: release what this one folded
                    job.Result.Dispose();
                    continue;
                }

                results[job.Slot] = job.Result;
                scan.Add(job.Slot, job.Result, job.Changed, job.Delta);
                if (job.StoredChild) storedChildren.Set(job.Slot);
            }

            if (error is not null) ExceptionDispatchInfo.Throw(error);
        }

        /// <summary>
        /// Sees the buckets <paramref name="queued"/> holds through and releases what each one folded,
        /// for a frame whose own descent threw and which will settle none of them.
        /// </summary>
        private static void Discard(in Fanout fanout, ref QueuedBuckets queued)
        {
            fanout.Wait(in queued);

            foreach (WorkStealingExecutor<Updater<TLayout>, BucketJob>.Node node in queued.Jobs)
            {
                node.Job.Result.Dispose();
                node.Job.Result = default;
            }
        }

        /// <inheritdoc cref="WorkStealingExecutor{TWorkerState, TJob}.IJobWorkerState.Complete"/>
        /// <remarks>Nothing: a thread's writes went to the store as it made them, the store bearing them all.</remarks>
        public void Complete()
        {
        }

        /// <inheritdoc cref="IDisposable.Dispose"/>
        /// <remarks><inheritdoc cref="Complete" path="/remarks"/></remarks>
        public void Dispose()
        {
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
        /// Where a frame hands the buckets it is not folding itself: the queue of the thread running it,
        /// which arrives with the job rather than being anything the updater keeps.
        /// </summary>
        private readonly struct Fanout(WorkStealingExecutor<Updater<TLayout>, BucketJob>.JobQueue queue)
        {
            /// <inheritdoc cref="WorkStealingExecutor{TWorkerState, TJob}.JobQueue.CanQueue"/>
            public bool CanQueue => queue.CanQueue;

            /// <inheritdoc cref="WorkStealingExecutor{TWorkerState, TJob}.JobQueue.TryQueue"/>
            public bool TryQueue(in BucketJob job, ref QueuedBuckets queued) => queue.TryQueue(in job, ref queued.Jobs);

            /// <inheritdoc cref="WorkStealingExecutor{TWorkerState, TJob}.JobQueue.Wait"/>
            public void Wait(in QueuedBuckets queued) => queue.Wait(in queued.Jobs);
        }

        /// <summary>The buckets one frame has handed out and not yet settled.</summary>
        /// <remarks>
        /// A frame starts with <c>default</c>, hands the same one to every <see cref="TryQueue"/> of that
        /// frame, and settles it once; the jobs themselves, and how they are waited for, are the queue's
        /// business.
        /// </remarks>
        internal struct QueuedBuckets
        {
            internal WorkStealingExecutor<Updater<TLayout>, BucketJob>.Handle Jobs;

            public readonly bool IsEmpty => Jobs.IsEmpty;
        }

        /// <summary>
        /// One bucket of one frame, waiting for a thread to fold it: the range of entries, where they go,
        /// and what the fold made of them.
        /// </summary>
        internal struct BucketJob
        {
            /// <summary>The boundary slot of the parent frame this job's result settles into.</summary>
            public int Slot { get; init; }

            public TrieNodeKey Key { get; init; }

            public int EntryStart { get; init; }

            public int EntryCount { get; init; }

            public int BucketStart { get; init; }

            public int BucketLength { get; init; }

            public int BranchDepth { get; init; }

            /// <summary>The node the parent's boundary slot holds, borrowed from the encoding the parent frame is reading.</summary>
            public Occupant Occupant { get; init; }

            public NodeResult Result { get; set; }

            public bool Changed { get; set; }

            public PbtSubtreeStats Delta { get; set; }

            /// <inheritdoc cref="ApplyKeyedChild" path="/param[@name='storedChild']"/>
            public bool StoredChild { get; set; }
        }
    }
}
