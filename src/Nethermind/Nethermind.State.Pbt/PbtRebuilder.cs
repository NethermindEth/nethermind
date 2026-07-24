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
public sealed class PbtRebuilder(IPbtPersistence target, ILogManager logManager, IPbtConfig config)
{
    private const int DefaultFlushEntryInterval = 2_000_000;

    /// <summary>Leaves buffered before a window is folded into the tree and committed.</summary>
    internal int FlushEntryInterval { get; init; } = config.ImportWindowSize > 0 ? config.ImportWindowSize : DefaultFlushEntryInterval;

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

        ValueHash256 root = default; // empty tree is 32 zero bytes
        long stems = 0;

        // Progress is logged off the just-committed window so throughput tracks durable work rather
        // than reads racing ahead.
        async Task FlushLoop()
        {
            long entries = 0;
            Stem lastStem = default;
            double entriesPerSec = 0, stemsPerSec = 0;
            long loggedEntries = 0, loggedStems = 0;
            Stopwatch sinceLog = Stopwatch.StartNew();

            // Entries stream in ascending stem order, so the current stem's position in the key space is
            // the completion fraction (stems are BLAKE3 digests, so the top 64 bits estimate it well).
            // CurrentValue is driven by the entry count instead, only so it moves every window: a stem
            // holding many leaves keeps the percentage fixed, and the logger drops a repeated line
            // unless CurrentValue changes — which would stall the log through the slowest part.
            ProgressLogger progress = new("PBT rebuild", logManager);
            progress.SetFormat(_ =>
            {
                float percentage = Math.Clamp(BinaryPrimitives.ReadUInt64BigEndian(lastStem.Bytes) / (float)ulong.MaxValue, 0, 1);
                return $"PBT rebuild {percentage.ToString("P2", CultureInfo.InvariantCulture),8} {Progress.GetMeter(percentage, 1)} | " +
                    $"{entries,15:N0} leaf ({entriesPerSec,8:N0}/s) | " +
                    $"{stems,13:N0} stem ({stemsPerSec,8:N0}/s) | at {lastStem}";
            });
            progress.Reset(0, 0);

            await foreach (FlushBatch batch in flushChannel.Reader.ReadAllAsync(pipelineCts.Token))
            {
                root = FlushAndCommit(batch.WriteBatch, root, batch.Changes, out PbtSubtreeStats stemDelta);
                stems += stemDelta.StemCount;
                (entries, lastStem) = (batch.Entries, batch.LastStem);

                double secs = sinceLog.Elapsed.TotalSeconds;
                if (secs > 0)
                {
                    entriesPerSec = (entries - loggedEntries) / secs;
                    stemsPerSec = (stems - loggedStems) / secs;
                }
                (loggedEntries, loggedStems) = (entries, stems);
                sinceLog.Restart();

                progress.Update((ulong)entries);
                progress.LogProgress();
            }
        }

        Task flusher = Task.Run(async () =>
        {
            try { await FlushLoop(); }
            catch { pipelineCts.Cancel(); throw; } // unblock a consumer parked on the full channel
        });

        // never pooled, so a Dispose would be a no-op
        PbtWriteBatchBuilder builder = new();
        IPbtPersistence.IWriteBatch? writeBatch = target.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, default, WriteFlags.DisableWAL);
        int pending = 0;
        long entries = 0;
        Stem lastStem = default;

        try
        {
            await foreach (ArrayPoolList<RebuildEntry> chunk in source.ReadAllAsync(pipelineCts.Token))
            {
                using (chunk)
                {
                    // the indexer rather than a span: the window seal awaits mid-chunk
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        RebuildEntry entry = chunk[i];
                        lastStem = entry.Stem;
                        builder.SetLeaf(entry.Stem, entry.SubIndex, entry.Leaf);
                        entries++;

                        if (++pending >= FlushEntryInterval)
                        {
                            // draining pre-buckets the batch, so the fold skips the top-level partitioning; a
                            // drained batch lost to a faulting flusher only drops its pooled maps to the GC
                            await flushChannel.Writer.WriteAsync(new FlushBatch(builder.DrainToWriteBatch(config.TrieNodeLayout.Tiling()), writeBatch!, entries, lastStem), pipelineCts.Token);
                            writeBatch = target.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, default, WriteFlags.DisableWAL);
                            pending = 0;
                        }
                    }
                }
            }

            // seal the final (possibly empty) window; the flusher owns its write batch from here
            await flushChannel.Writer.WriteAsync(new FlushBatch(builder.DrainToWriteBatch(config.TrieNodeLayout.Tiling()), writeBatch!, entries, lastStem), pipelineCts.Token);
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
        using (target.CreateWriteBatch(StateId.PreGenesis, targetState, root, WriteFlags.None)) { }

        if (_logger.IsInfo) _logger.Info($"PBT rebuild complete at {targetState}: {entries} leaves, {stems} stems, tree root {root}");
        return root;
    }

    /// <summary>A full window handed from the consumer to the flush worker, with the progress counters as of when it was sealed.</summary>
    private readonly record struct FlushBatch(
        PbtWriteBatch Changes,
        IPbtPersistence.IWriteBatch WriteBatch,
        long Entries,
        Stem LastStem);

    /// <summary>
    /// Folds the drained window into the tree on top of <paramref name="currentRoot"/> and commits it.
    /// <paramref name="stemDelta"/> reports the change this window makes to the tree's stem count
    /// (zero for an empty window).
    /// </summary>
    private ValueHash256 FlushAndCommit(IPbtPersistence.IWriteBatch writeBatch, ValueHash256 currentRoot, PbtWriteBatch changes, out PbtSubtreeStats stemDelta)
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
                currentRoot = TrieUpdater.UpdateRoot(store, currentRoot, changes, PooledRefCountingMemoryProvider.Instance, config.TrieNodeLayout, config.RootFoldConcurrency, out stemDelta);
            }
        }

        writeBatch.Dispose(); // atomic commit of this window's leaves and nodes, when it had any
        return currentRoot;
    }
}
