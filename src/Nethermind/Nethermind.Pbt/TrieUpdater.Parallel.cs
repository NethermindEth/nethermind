// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
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

        /// <summary>Bounds on <see cref="WriteBufferCapacity"/>: enough that a small fold never grows, capped so a huge one does not rent per worker what only one of them will fill.</summary>
        private const int MinWriteBufferCapacity = 64;

        /// <inheritdoc cref="MinWriteBufferCapacity"/>
        private const int MaxWriteBufferCapacity = 4096;

        private readonly WorkStealingExecutor<Updater, Worker, Worker.BucketJob> _executor;

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

            WriteBufferCapacity = Math.Clamp(changes.Count / workerCount, MinWriteBufferCapacity, MaxWriteBufferCapacity);

            // settled before the executor builds the workers, which read it to decide whether they need
            // a write buffer at all
            IsParallel = workerCount > 1;
            _executor = new WorkStealingExecutor<Updater, Worker, Worker.BucketJob>(
                workerCount, this, static (updater, lane) => new Worker(updater, lane), Worker.Fold);
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

        public bool IsParallel { get; }

        /// <summary>
        /// What one worker's buffered writes start out sized for: an even share of the batch, since a
        /// fold buffers a leaf blob per stem it touches. A worker handed more than its share grows into
        /// the pool rather than out of it.
        /// </summary>
        public int WriteBufferCapacity { get; }

        /// <summary>Folds <paramref name="changes"/> into the tree at <paramref name="currentRoot"/> and returns the new root.</summary>
        /// <remarks>
        /// The calling thread runs the root frame on the executor's main lane; the rest of the threads
        /// only ever run what it and its descendants hand out.
        /// </remarks>
        public ValueHash256 Run(in ValueHash256 currentRoot, PbtWriteBatch changes, out PbtSubtreeStats delta)
        {
            _executor.Start();

            bool folded = false;
            try
            {
                ValueHash256 root = _executor.Workers[0].Run(currentRoot, changes, out delta);
                folded = true;
                return root;
            }
            finally
            {
                _executor.Complete();
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
            foreach (Worker worker in _executor.Workers) worker.FlushWrites(commit);
        }
    }

    private sealed partial class Worker
    {
        /// <summary>The lane this worker spawns and joins on; a serial fold's lane cannot spawn at all.</summary>
        private readonly WorkStealingExecutor<Updater, Worker, BucketJob>.Lane _lane = lane;

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
        /// <para>
        /// Pooled, and sized for the share of the batch this worker can expect: a fold buffers one entry
        /// per stem it touches, so the lists are the largest thing it allocates.
        /// </para>
        /// </remarks>
        private readonly ArrayPoolList<(TrieNodeKey Key, byte[]? Node)>? _nodeWrites =
            updater.IsParallel ? new ArrayPoolList<(TrieNodeKey, byte[]?)>(updater.WriteBufferCapacity) : null;

        /// <inheritdoc cref="_nodeWrites"/>
        private readonly ArrayPoolList<(Stem Stem, byte[]? Blob)>? _blobWrites =
            updater.IsParallel ? new ArrayPoolList<(Stem, byte[]?)>(updater.WriteBufferCapacity) : null;

        /// <summary>
        /// Whether this frame may hand any of its buckets out at all: the fold is a parallel one, and the
        /// frame branches. A frame whose entries all fall in one bucket would be handing its whole range
        /// over and then waiting on it, which buys nothing.
        /// </summary>
        private bool MaySpawn(uint touchedBitmask) => _lane.CanSpawn && !BitOperations.IsPow2(touchedBitmask);

        /// <summary>
        /// Queues <paramref name="bucket"/> as a job for whichever thread reaches it first, and returns
        /// the node holding it; <c>null</c> when the queue is full, leaving the frame to fold the bucket
        /// itself.
        /// </summary>
        /// <param name="next">The node this frame spawned before it, which this one is chained ahead of.</param>
        private WorkStealingExecutor<Updater, Worker, BucketJob>.Node? TrySpawn(
            int slot, in TrieNodeKey childKey, Span<PbtWriteBatch.StemEntry> bucket, in Occupant occupant,
            scoped BucketPlan childPlan, WorkStealingExecutor<Updater, Worker, BucketJob>.Node? next)
        {
            ReadOnlySpan<int> precalculated = childPlan.Precalculated;
            BucketJob job = new()
            {
                Slot = slot,
                Key = childKey,
                EntryStart = IndexOf(updater.Entries, bucket),
                EntryCount = bucket.Length,
                BucketStart = precalculated.IsEmpty ? 0 : IndexOf(updater.Buckets!, precalculated),
                BucketLength = precalculated.Length,
                BranchDepth = childPlan.BranchDepth,
                Occupant = occupant,
            };

            return _lane.TrySpawn(in job, next);
        }

        /// <summary>
        /// Waits out the jobs <paramref name="spawned"/> chains and settles each one's result into the
        /// frame's boundary.
        /// </summary>
        /// <param name="queueMark">Where this worker's lane stood before the frame spawned anything.</param>
        private void Join(
            WorkStealingExecutor<Updater, Worker, BucketJob>.Node spawned, long queueMark,
            Span<NodeResult> results, ref BoundaryScan scan, ref uint storedChildBitmask)
        {
            _lane.Join(spawned, queueMark);

            Exception? error = null;
            for (WorkStealingExecutor<Updater, Worker, BucketJob>.Node? node = spawned; node is not null; node = node.Next)
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
                if (job.StoredChild) storedChildBitmask |= 1u << job.Slot;
            }

            if (error is not null) ExceptionDispatchInfo.Throw(error);
        }

        /// <summary>Folds one queued bucket, whichever thread got to it.</summary>
        public static void Fold(Updater updater, Worker worker, ref BucketJob job)
        {
            BucketPlan plan = new(
                job.BucketLength == 0 ? default : updater.Buckets!.AsSpan(job.BucketStart, job.BucketLength),
                job.BranchDepth);
            worker.ApplyKeyedChild(
                job.Key, updater.Entries.AsSpan(job.EntryStart, job.EntryCount), job.Occupant, plan,
                out NodeResult result, out bool changed, out PbtSubtreeStats delta, out bool storedChild);

            job.Result = result;
            job.Changed = changed;
            job.Delta = delta;
            job.StoredChild = storedChild;
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
                foreach ((TrieNodeKey key, byte[]? node) in _nodeWrites.AsSpan()) updater.Store.SetTrieNode(key, node);
                foreach ((Stem stem, byte[]? blob) in _blobWrites!.AsSpan()) updater.Store.SetLeafBlob(stem, blob);
            }

            _nodeWrites.Dispose();
            _blobWrites!.Dispose();
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
