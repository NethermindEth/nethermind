// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.ScopeProvider;

namespace Nethermind.State.Pbt;

/// <summary>
/// Bulk-builds a PBT state from a chunked stream of decoded <see cref="RebuildEntry"/> records
/// (e.g. from an iterated preimage-flat database), computing the EIP-8297 root without processing any
/// blocks. It is the single consumer of the stream; the caller (producer) scans and decodes the source.
/// </summary>
/// <remarks>
/// The rebuild runs in bounded windows to cap memory. The flat account and slot columns are already
/// populated by the caller — this only builds the tree over them. Data windows are
/// written <see cref="StateId.PreGenesis"/> → <see cref="StateId.PreGenesis"/> with
/// <see cref="WriteFlags.DisableWAL"/> — the persisted-state pointer stays pre-genesis so a crash
/// mid-rebuild leaves the state unpopulated and the next run restarts cleanly, which is also what
/// makes skipping the WAL safe.
/// Because each window folds against the previously committed windows, a stem split across windows is
/// merged correctly (the updater reads its prior leaf blob and folds the new leaves in).
/// </remarks>
public sealed class PbtRebuilder(PbtRocksDbPersistence target, ILogManager logManager, IPbtConfig config)
{
    private const int DefaultFlushEntryInterval = 2_000_000;

    /// <summary>Leaves buffered before a window is folded into the tree and committed.</summary>
    internal int FlushEntryInterval { get; init; } = config.ImportWindowSize > 0 ? config.ImportWindowSize : DefaultFlushEntryInterval;

    /// <summary>Stems buffered before a window is folded, whichever of the two bounds a window reaches first.</summary>
    /// <remarks>
    /// A leaf bound alone does not bound the batch the window drains to: a window of single-leaf stems
    /// holds one entry per leaf. Past <see cref="PbtWriteBatch.MaxPooledStems"/> that batch's entry list
    /// stops being poolable, so every window would allocate and abandon one large object of it.
    /// </remarks>
    internal int MaxWindowStems { get; init; } = PbtWriteBatch.MaxPooledStems;

    private readonly ILogger _logger = logManager.GetClassLogger<PbtRebuilder>();

    /// <summary>Rebuilds the tree from <paramref name="source"/> and returns the EIP-8297 root it folded to.</summary>
    /// <param name="targetState">
    /// The state the rebuilt tree represents, keyed as the rest of the node addresses it: by the root
    /// its block's header claims, which is the source database's. The tree's own root — this method's
    /// return value — is recorded beside it, so a node starting on the result finds its state by header
    /// and still folds the next block on the right root.
    /// </param>
    /// <remarks>
    /// Reading and folding run as a pipeline: this consumer accumulates each window and hands the full
    /// one to a single flush worker over a bounded (capacity-1) channel, so the next window fills while
    /// the worker folds and commits the current one. The fold stays sequential — each window folds on
    /// the previous root — so exactly one worker drains the channel in order, and it alone touches the
    /// target database.
    /// </remarks>
    public async Task<ValueHash256> Rebuild(ChannelReader<ArrayPoolList<RebuildEntry>> source, StateId targetState, CancellationToken cancellationToken)
    {
        using CancellationTokenSource pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Channel<FlushBatch> flushChannel = Channel.CreateBounded<FlushBatch>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        PbtPartitionRoots roots = PbtPartitionRoots.Empty;
        long stems = 0;

        // Progress is logged off the just-committed window so throughput tracks durable work rather
        // than reads racing ahead.
        async Task FlushLoop()
        {
            long entries = 0;
            double entriesPerSec = 0, stemsPerSec = 0;
            long loggedEntries = 0, loggedStems = 0;
            Stopwatch sinceLog = Stopwatch.StartNew();
            PartitionProgress[] progress = CreatePartitionProgressLoggers();

            await foreach (FlushBatch batch in flushChannel.Reader.ReadAllAsync(pipelineCts.Token))
            {
                roots = FlushAndCommit(batch.WriteBatch, roots, batch.Changes, out PbtSubtreeStats stemDelta);
                stems += stemDelta.StemCount;
                entries = batch.Entries;

                double secs = sinceLog.Elapsed.TotalSeconds;
                if (secs > 0)
                {
                    entriesPerSec = (entries - loggedEntries) / secs;
                    stemsPerSec = (stems - loggedStems) / secs;
                }
                (loggedEntries, loggedStems) = (entries, stems);
                sinceLog.Restart();

                foreach (PbtPartition partition in PbtPartitions.All)
                {
                    if (!batch.HasEntries[(int)partition]) continue;

                    PartitionProgress partitionProgress = progress[(int)partition];
                    partitionProgress.LastStem = batch.LastStems[(int)partition];
                    partitionProgress.Entries = entries;
                    partitionProgress.EntriesPerSec = entriesPerSec;
                    partitionProgress.Stems = stems;
                    partitionProgress.StemsPerSec = stemsPerSec;
                    partitionProgress.Logger.Update((ulong)entries);
                    partitionProgress.Logger.LogProgress();
                }
            }
        }

        Task flusher = Task.Run(async () =>
        {
            try { await FlushLoop(); }
            catch { pipelineCts.Cancel(); throw; } // unblock a consumer parked on the full channel
        });

        PbtWriteBatchBuilder builder = new();
        StemGroup group = new();
        IPbtPersistence.IWriteBatch? writeBatch = target.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, PbtPartitionRoots.Empty, WriteFlags.DisableWAL);
        int pending = 0, pendingStems = 0;
        long entries = 0;
        Stem[] lastStems = new Stem[PbtPartitions.Count];
        bool[] hasEntries = new bool[PbtPartitions.Count];

        try
        {
            await foreach (ArrayPoolList<RebuildEntry> chunk in source.ReadAllAsync(pipelineCts.Token))
            {
                using (chunk)
                {
                    // Sealing can await mid-chunk, so a span cannot stay live here.
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        RebuildEntry entry = chunk[i];
                        if (!group.Continues(entry))
                        {
                            group.Flush(builder);

                            // Sealing here rather than a leaf into a stem is what keeps a window a whole
                            // number of stems: a stem split over two windows costs the second a
                            // read-modify-write of the leaf blob the first one wrote.
                            if (pending >= FlushEntryInterval || pendingStems >= MaxWindowStems)
                            {
                                // A drained batch lost to a faulting flusher only drops its pooled maps to the GC.
                                await flushChannel.Writer.WriteAsync(new FlushBatch(builder.DrainToWriteBatches(), writeBatch!, entries, lastStems, hasEntries), pipelineCts.Token);
                                writeBatch = target.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, PbtPartitionRoots.Empty, WriteFlags.DisableWAL);
                                (pending, pendingStems) = (0, 0);
                                lastStems = new Stem[PbtPartitions.Count];
                                hasEntries = new bool[PbtPartitions.Count];
                            }

                            // groups, not distinct stems: an unordered source repeating a stem overshoots
                            // the count and seals a window early, which costs nothing the bound is for
                            pendingStems++;
                        }

                        group.Add(entry);
                        PbtPartition partition = PbtPartitions.Of(entry.Stem);
                        lastStems[(int)partition] = entry.Stem;
                        hasEntries[(int)partition] = true;
                        entries++;
                        pending++;
                    }
                }
            }

            group.Flush(builder);

            // seal the final (possibly empty) window; the flusher owns its write batch from here
            await flushChannel.Writer.WriteAsync(new FlushBatch(builder.DrainToWriteBatches(), writeBatch!, entries, lastStems, hasEntries), pipelineCts.Token);
            writeBatch = null;
            flushChannel.Writer.Complete();
            await flusher;
        }
        catch
        {
            flushChannel.Writer.TryComplete();
            writeBatch?.Dispose(); // the un-sealed batch we still own, if any
            builder.Reset(); // return the un-drained maps, if any
            await flusher; // if the flusher faulted, this rethrows its (root-cause) exception
            throw;
        }

        // the data windows skipped the WAL, so make them durable before the pointer that claims them
        target.Flush();

        // atomically advance the persisted-state pointer to the rebuilt state
        using (target.CreateWriteBatch(StateId.PreGenesis, targetState, roots, WriteFlags.None)) { }

        if (_logger.IsInfo) _logger.Info($"PBT rebuild complete at {targetState}: {entries} leaves, {stems} stems, tree root {roots.Root}");
        return roots.Root;
    }

    /// <summary>
    /// The leaves of one stem, gathered from the consecutive entries carrying them so that the stem
    /// reaches the builder complete, in one rent of one change map.
    /// </summary>
    /// <remarks>
    /// A group runs while the entries stay on one stem and their sub-indices ascend — which a sorted
    /// source gives for free and an unsorted one simply breaks into groups of one, both of which
    /// <see cref="PbtWriteBatchBuilder.SetLeaves"/> takes. Ascending byte sub-indices are also what bound
    /// a group by the stem's own width.
    /// </remarks>
    private sealed class StemGroup
    {
        private readonly byte[] _subIndices = new byte[PbtKeyDerivation.StemSubtreeWidth];
        private readonly ValueHash256[] _values = new ValueHash256[PbtKeyDerivation.StemSubtreeWidth];
        private Stem _stem;
        private int _count;

        /// <summary>Whether <paramref name="entry"/> extends the open group rather than starting a new one.</summary>
        public bool Continues(in RebuildEntry entry) =>
            _count > 0 && entry.Stem == _stem && entry.SubIndex > _subIndices[_count - 1];

        public void Add(in RebuildEntry entry)
        {
            _stem = entry.Stem;
            _subIndices[_count] = entry.SubIndex;
            _values[_count] = entry.Leaf;
            _count++;
        }

        /// <summary>Hands the open group to <paramref name="builder"/> and empties it; a no-op when none is open.</summary>
        public void Flush(PbtWriteBatchBuilder builder)
        {
            builder.SetLeaves(_stem, _subIndices.AsSpan(0, _count), _values.AsSpan(0, _count));
            _count = 0;
        }
    }

    private PartitionProgress[] CreatePartitionProgressLoggers()
    {
        PartitionProgress[] progress = new PartitionProgress[PbtPartitions.Count];
        foreach (PbtPartition partition in PbtPartitions.All)
        {
            PartitionProgress partitionProgress = new(partition, new ProgressLogger("PBT rebuild", logManager));
            partitionProgress.Logger.SetFormat(_ => FormatProgress(partitionProgress));
            partitionProgress.Logger.Reset(0, 0);
            progress[(int)partition] = partitionProgress;
        }

        return progress;
    }

    private static string FormatProgress(PartitionProgress progress)
    {
        int rootDepth = PbtPartitions.RootDepth(progress.Partition);
        ulong localRange = 1UL << (64 - rootDepth);
        float percentage = (BinaryPrimitives.ReadUInt64BigEndian(progress.LastStem.Bytes) & (localRange - 1)) / (float)localRange;
        return $"PBT rebuild {progress.Partition,-7} {percentage.ToString("P2", CultureInfo.InvariantCulture),8} {Progress.GetMeter(percentage, 1)} | " +
            $"{progress.Entries,15:N0} leaf ({progress.EntriesPerSec,8:N0}/s) | " +
            $"{progress.Stems,13:N0} stem ({progress.StemsPerSec,8:N0}/s) | at {progress.LastStem}";
    }

    private sealed class PartitionProgress(PbtPartition partition, ProgressLogger logger)
    {
        public PbtPartition Partition { get; } = partition;
        public ProgressLogger Logger { get; } = logger;
        public Stem LastStem { get; set; }
        public long Entries { get; set; }
        public double EntriesPerSec { get; set; }
        public long Stems { get; set; }
        public double StemsPerSec { get; set; }
    }

    /// <summary>A full window handed from the consumer to the flush worker, with the progress counters as of when it was sealed.</summary>
    private readonly record struct FlushBatch(
        PbtWriteBatchSet Changes,
        IPbtPersistence.IWriteBatch WriteBatch,
        long Entries,
        Stem[] LastStems,
        bool[] HasEntries);

    /// <summary>
    /// Folds the drained window into the tree on top of <paramref name="currentRoot"/> and commits it.
    /// <paramref name="stemDelta"/> reports the change this window makes to the tree's stem count
    /// (zero for an empty window).
    /// </summary>
    private PbtPartitionRoots FlushAndCommit(
        IPbtPersistence.IWriteBatch writeBatch, PbtPartitionRoots currentRoots, PbtWriteBatchSet changes,
        out PbtSubtreeStats stemDelta)
    {
        stemDelta = default;
        using (changes)
        {
            if (changes.Count > 0)
            {
                // a fresh reader sees the previously committed windows; the updater reads their prior nodes
                // and blobs and writes the new ones into this window's still-open batch
                using IPbtPersistence.IReader reader = target.CreateReader();
                PersistenceBackedPbtStore store = new(reader, writeBatch);
                currentRoots = TrieUpdater.UpdateRoot(store, currentRoots, changes, PooledRefCountingMemoryProvider.Instance, config.TrieNodeLayout, config.RootFoldConcurrency, out stemDelta);
            }
        }

        writeBatch.Dispose();
        return currentRoots;
    }
}
