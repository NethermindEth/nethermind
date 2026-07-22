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

    /// <summary>
    /// Bounds on a thread's buffered writes: enough that a small fold never grows, capped so a huge one
    /// does not rent per thread what only one of them will fill.
    /// </summary>
    private const int MinWriteBufferCapacity = 64;

    /// <inheritdoc cref="MinWriteBufferCapacity"/>
    private const int MaxWriteBufferCapacity = 4096;

    /// <summary>
    /// Folds <paramref name="changes"/> into the tree at <paramref name="currentRoot"/> across as many
    /// threads as the batch and <paramref name="concurrency"/> between them allow, and returns the new root.
    /// </summary>
    /// <remarks>
    /// One <see cref="Updater"/> per thread, each holding its own buffered writes and the lane it hands
    /// buckets out through. The calling thread runs the root frame on the first of them, and the rest
    /// only ever run what it and its descendants hand out. Every write they buffered is replayed here,
    /// on the calling thread, which is what keeps <see cref="IPbtStore"/> a single-threaded interface.
    /// </remarks>
    /// <param name="concurrency"><inheritdoc cref="UpdateRoot" path="/param[@name='concurrency']"/></param>
    private static ValueHash256 Fold(
        IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch changes,
        IRefCountingMemoryProvider memoryProvider, PbtGroupFormat writeFormat, int concurrency,
        out PbtSubtreeStats delta)
    {
        int threadCount = ResolveThreadCount(concurrency, changes.Count);

        // The threshold splits the batch into about TargetJobsPerThread jobs per thread, so that the
        // queues hold enough to steal from without the descent stopping to spawn every other frame; a
        // fold on the calling thread alone takes the spawn out of the descent entirely.
        int minSpawnEntries = threadCount == 1
            ? int.MaxValue
            : Math.Max(HardMinimumStems, changes.Count / threadCount / TargetJobsPerThread);
        int writeBufferCapacity = Math.Clamp(changes.Count / threadCount, MinWriteBufferCapacity, MaxWriteBufferCapacity);

        // held as arrays rather than as the batch: a bucket is a range of entry indices, which is what
        // lets one thread hand it to another
        PbtWriteBatch.StemEntry[] entries = changes.EntriesArray;
        int[]? buckets = changes.BucketsArray;

        WorkStealingExecutor<Updater, Updater.BucketJob> executor = new(
            threadCount,
            lane => new Updater(store, memoryProvider, writeFormat, entries, buckets, minSpawnEntries, writeBufferCapacity, lane),
            Updater.RunJob);

        executor.Start();

        bool folded = false;
        try
        {
            ValueHash256 root = executor.Workers[0].Run(currentRoot, changes, out delta);
            folded = true;
            return root;
        }
        finally
        {
            executor.Complete();

            // Every thread is quiescent by now: the root frame's join settled the last job before this
            // was reached, and a job's completion publishes its writes to whoever waits on it. A fold
            // that threw keeps none of them.
            foreach (Updater updater in executor.Workers) updater.FlushWrites(folded);
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

    private sealed partial class Updater
    {
        /// <summary>What the executor runs a queued job through, which is the fold of one bucket.</summary>
        internal static readonly WorkStealingExecutor<Updater, BucketJob>.JobRunner RunJob =
            static (Updater updater, ref BucketJob job) => updater.FoldBucket(ref job);

        /// <summary>The lane this thread spawns and joins on; a serial fold's lane cannot spawn at all.</summary>
        private readonly WorkStealingExecutor<Updater, BucketJob>.Lane _lane = lane;

        /// <summary>
        /// The store writes this thread made, replayed by the calling thread once the fold is through;
        /// <c>null</c> for a fold on the calling thread alone, which writes straight through.
        /// </summary>
        /// <remarks>
        /// Buffering is what keeps <see cref="IPbtStore"/> a single-threaded interface: only the reads
        /// run concurrently, and nothing writes the store while they do. The two lists need no order
        /// between them — a node key and a stem name different things — and the writes of two threads
        /// need none either, their key ranges being disjoint. What is buffered is the value's own array
        /// rather than the buffer it was folded in, which goes back to the pool at the write: holding
        /// a fold's worth of rentals to the end of it would leave every thread renting fresh ones.
        /// <para>
        /// Pooled, and sized for the share of the batch one thread can expect: a fold buffers one entry
        /// per stem it touches, so the lists are the largest thing it allocates.
        /// </para>
        /// </remarks>
        private readonly ArrayPoolList<(TrieNodeKey Key, byte[]? Node)>? _nodeWrites =
            lane.CanSpawn ? new ArrayPoolList<(TrieNodeKey, byte[]?)>(writeBufferCapacity) : null;

        /// <inheritdoc cref="_nodeWrites"/>
        private readonly ArrayPoolList<(Stem Stem, byte[]? Blob)>? _blobWrites =
            lane.CanSpawn ? new ArrayPoolList<(Stem, byte[]?)>(writeBufferCapacity) : null;

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
        private WorkStealingExecutor<Updater, BucketJob>.Node? TrySpawn(
            int slot, in TrieNodeKey childKey, Span<PbtWriteBatch.StemEntry> bucket, in Occupant occupant,
            scoped BucketPlan childPlan, WorkStealingExecutor<Updater, BucketJob>.Node? next)
        {
            ReadOnlySpan<int> precalculated = childPlan.Precalculated;
            BucketJob job = new()
            {
                Slot = slot,
                Key = childKey,
                EntryStart = IndexOf(entries, bucket),
                EntryCount = bucket.Length,
                BucketStart = precalculated.IsEmpty ? 0 : IndexOf(buckets!, precalculated),
                BucketLength = precalculated.Length,
                BranchDepth = childPlan.BranchDepth,
                Occupant = occupant,
            };

            return _lane.TrySpawn(in job, next);
        }

        /// <summary>
        /// Settles the jobs <paramref name="spawned"/> chains, which the lane has seen through, into the
        /// frame's boundary — and rethrows on this thread whatever one of them threw on another.
        /// </summary>
        private void Settle(
            WorkStealingExecutor<Updater, BucketJob>.Node spawned, Span<NodeResult> results,
            ref BoundaryScan scan, ref uint storedChildBitmask)
        {
            Exception? error = null;
            for (WorkStealingExecutor<Updater, BucketJob>.Node? node = spawned; node is not null; node = node.Next)
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

        /// <summary>Folds one queued bucket, on whichever thread got to it.</summary>
        private void FoldBucket(ref BucketJob job)
        {
            BucketPlan plan = new(
                job.BucketLength == 0 ? default : buckets!.AsSpan(job.BucketStart, job.BucketLength),
                job.BranchDepth);
            ApplyKeyedChild(
                job.Key, entries.AsSpan(job.EntryStart, job.EntryCount), job.Occupant, plan,
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
            if (_nodeWrites is null) store.SetTrieNode(key, value);
            else _nodeWrites.Add((key, value));
        }

        /// <summary><inheritdoc cref="SetTrieNode" path="/summary"/></summary>
        /// <remarks><inheritdoc cref="SetTrieNode" path="/remarks"/></remarks>
        private void SetLeafBlob(in Stem stem, RefCountingMemory? blob)
        {
            byte[]? value = blob?.ToArrayAndRelease();
            if (_blobWrites is null) store.SetLeafBlob(stem, value);
            else _blobWrites.Add((stem, value));
        }

        /// <summary>Hands the store what this thread buffered, in the order it made the writes, or drops it.</summary>
        public void FlushWrites(bool commit)
        {
            if (_nodeWrites is null) return;

            if (commit)
            {
                foreach ((TrieNodeKey key, byte[]? node) in _nodeWrites.AsSpan()) store.SetTrieNode(key, node);
                foreach ((Stem stem, byte[]? blob) in _blobWrites!.AsSpan()) store.SetLeafBlob(stem, blob);
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
