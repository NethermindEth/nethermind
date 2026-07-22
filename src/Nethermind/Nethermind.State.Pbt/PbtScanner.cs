// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>The zone subtrees the statistics are grouped by, mirroring the leaf columns' split.</summary>
public enum PbtTreePartition
{
    /// <summary>The root group, which is above the zone split and so belongs to no one zone.</summary>
    Root,

    /// <summary>The account header zone, 0x0.</summary>
    Account,

    /// <summary>The content-addressed code zone, 0x1.</summary>
    Code,

    /// <summary>The storage zones, 0x8-0xF.</summary>
    Storage,
}

/// <summary>
/// Counts what the persisted PBT columns hold: the shape of the stem trie by depth and zone, and how
/// many nodes each of the store's space optimizations elides.
/// </summary>
/// <remarks>
/// A flat sweep of the columns rather than a descent from the root. Every statistic here follows from
/// one entry's key and value alone — a trie node key <em>is</em> its path and depth, its column names
/// its zone, and a group's bitmaps pin its whole shape, the runs it holds included — so the
/// scan needs no parent context and does no random reads. What it therefore cannot see is anything
/// only a descent establishes: it does not check that a node is reachable from the root, nor refold
/// any hash, so an orphaned blob is counted rather than reported.
/// </remarks>
public sealed class PbtScanner(IColumnsDb<PbtColumns> db, IPbtConfig config, ILogManager logManager)
{
    /// <summary>
    /// Entries a worker counts locally before publishing them, and so also how often it checks for
    /// cancellation; the sweep is otherwise a tight loop over hundreds of millions of rows.
    /// </summary>
    private const int ProgressPublishInterval = 100_000;

    private static readonly TimeSpan ProgressLogInterval = TimeSpan.FromSeconds(5);

    /// <summary>Ranges per worker, so that work stealing can even out the uneven ones.</summary>
    private const int RangesPerWorker = 16;

    /// <summary>Key prefix the ranges are split over, the first two bytes of a key.</summary>
    private const int PrefixSpace = 1 << 16;

    private readonly ILogger _logger = logManager.GetClassLogger<PbtScanner>();

    private delegate void ScanEntry(PbtScanReport report, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value);

    public async Task<PbtScanReport> Scan(CancellationToken cancellationToken)
    {
        PbtScanReport report = new();
        int workerCount = config.ScanTreeConcurrency > 0 ? config.ScanTreeConcurrency : Environment.ProcessorCount;
        int rangeCount = Math.Min(workerCount * RangesPerWorker, PrefixSpace);
        byte[][] trieBounds = PrefixBounds(rangeCount, TrieNodeKey.Length);
        byte[][] leafBounds = PrefixBounds(rangeCount, Stem.Length);

        await ScanColumn(PbtColumns.AccountTrieNodes, trieBounds, TrieNodeScanner(PbtTreePartition.Account), report, workerCount, cancellationToken);
        await ScanColumn(PbtColumns.CodeTrieNodes, trieBounds, TrieNodeScanner(PbtTreePartition.Code), report, workerCount, cancellationToken);
        await ScanColumn(PbtColumns.StorageTrieNodes, trieBounds, TrieNodeScanner(PbtTreePartition.Storage), report, workerCount, cancellationToken);
        await ScanColumn(PbtColumns.AccountLeaves, leafBounds, static (report, _, value) => ScanLeafBlob(value, report.AccountLeaves), report, workerCount, cancellationToken);
        await ScanColumn(PbtColumns.CodeLeaves, leafBounds, static (report, _, value) => ScanLeafBlob(value, report.CodeLeaves), report, workerCount, cancellationToken);
        await ScanColumn(PbtColumns.StorageLeaves, leafBounds, static (report, _, value) => ScanLeafBlob(value, report.StorageLeaves), report, workerCount, cancellationToken);

        return report;
    }

    /// <summary>
    /// Sweeps one column in parallel, counting every entry into <paramref name="report"/>.
    /// </summary>
    /// <remarks>
    /// The workers claim the ranges <paramref name="bounds"/> delimits on demand rather than by an
    /// up-front assignment, because the ranges are wildly uneven. Each worker owns a range, the
    /// iterator over it and a report of its own, so nothing is shared until the reports are folded
    /// together here; the counters the progress ticker samples are all that cross threads.
    /// </remarks>
    private async Task ScanColumn(
        PbtColumns columnName,
        byte[][] bounds,
        ScanEntry scanEntry,
        PbtScanReport report,
        int workerCount,
        CancellationToken cancellationToken)
    {
        IDb column = db.GetColumnDb(columnName);
        if (column is not ISortedKeyValueStore sorted)
        {
            throw new InvalidOperationException($"The PBT {columnName} column is a {column.GetType().Name}, which cannot be range scanned.");
        }

        int rangeCount = bounds.Length - 1;
        int nextRange = -1, doneRanges = 0;
        long scanned = 0;

        PbtScanReport[] shards = new PbtScanReport[workerCount];
        for (int i = 0; i < shards.Length; i++) shards[i] = new PbtScanReport();

        void ScanRanges(PbtScanReport shard)
        {
            long pending = 0;
            int range;
            while ((range = Interlocked.Increment(ref nextRange)) < rangeCount)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using ISortedView view = sorted.GetViewBetween(bounds[range], bounds[range + 1]);
                while (view.MoveNext())
                {
                    ReadOnlySpan<byte> value = view.CurrentValue;
                    if (value.IsEmpty) continue;   // an empty value is a removal marker, never stored

                    scanEntry(shard, view.CurrentKey, value);

                    if (++pending >= ProgressPublishInterval)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Interlocked.Add(ref scanned, pending);
                        pending = 0;
                    }
                }

                Interlocked.Increment(ref doneRanges);
            }

            Interlocked.Add(ref scanned, pending);
        }

        // ProgressLogger is neither thread-safe nor self-throttling, so one ticker owns it and samples
        // the counters the workers publish, rather than the workers logging as they go
        ProgressLogger progress = CreateProgressLogger(columnName, column, () => Volatile.Read(ref doneRanges) / (float)rangeCount);
        using CancellationTokenSource loggingCts = new();
        Task logging = Task.Run(async () =>
        {
            try
            {
                using PeriodicTimer timer = new(ProgressLogInterval);
                while (await timer.WaitForNextTickAsync(loggingCts.Token))
                {
                    progress.Update((ulong)Interlocked.Read(ref scanned));
                    progress.LogProgress();
                }
            }
            catch (OperationCanceledException) { /* the sweep finished */ }
        }, CancellationToken.None);

        Stopwatch sweeping = Stopwatch.StartNew();
        Task[] workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            PbtScanReport shard = shards[i];
            workers[i] = Task.Run(() => ScanRanges(shard), cancellationToken);
        }

        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            await loggingCts.CancelAsync();
            await logging;
        }

        foreach (PbtScanReport shard in shards) report.MergeFrom(shard);

        progress.Update((ulong)scanned);
        progress.MarkEnd();
        if (_logger.IsInfo) _logger.Info($"PBT scan {columnName}: {scanned:N0} entries in {sweeping.Elapsed:hh\\:mm\\:ss} at {progress.TotalPerSecond:N0}/s");
    }

    /// <summary>Counts the entries of one zone's trie node column into that zone's statistics.</summary>
    /// <remarks>The depth-0 root shares the account column but spans every zone, so it is counted apart.</remarks>
    private static ScanEntry TrieNodeScanner(PbtTreePartition partition) => (report, key, value) =>
    {
        // the key is the node's position: the zero-padded path, then the depth byte
        int depth = key[Stem.Length];

        ScanTrieNode(value, depth, partition, report);
    };

    /// <summary>
    /// Counts one trie node blob as the nodes it holds — the group at the key's depth, the runs hanging
    /// off it, and for a wrapper the children one group below it, with their own runs — so every
    /// histogram here reads as it did before they shared a blob.
    /// </summary>
    private static void ScanTrieNode(ReadOnlySpan<byte> value, int depth, PbtTreePartition partition, PbtScanReport report)
    {
        PbtScanReport.TrieNodeStats stats = report[depth == 0 ? PbtTreePartition.Root : partition];

        PbtTrieNodeWrapper wrapper = PbtTrieNodeWrapper.Decode(value, out PbtTrieNodeGroup group);
        ReadOnlySpan<byte> groupBytes = value[wrapper.Group];
        ScanGroup(group, groupBytes.Length, depth, stats, report);
        if (wrapper.IsEmpty) return;

        stats.WrapperCount++;
        stats.WrappersByDepth[depth]++;
        stats.WrapperBlobBytesByDepth[depth] += value.Length;
        int childDepth = depth + PbtLayout.TrieNodeGroupLevelsPerGroup;
        int childBytes = 0;
        for (int slot = 0; slot < PbtLayout.TrieNodeGroupBoundarySlots; slot++)
        {
            ReadOnlySpan<byte> child = value[wrapper.Child(slot, group)];
            if (child.IsEmpty) continue;

            stats.WrappedChildCount++;
            stats.WrappedChildrenByDepth[depth]++;
            childBytes += child.Length;

            // a wrapped child is a bare group, never a wrapper of its own: the level a wrapper holds is
            // the level that does not itself wrap (see PbtLayout.IsWrappingDepth)
            ScanGroup(PbtTrieNodeGroup.Decode(child), child.Length, childDepth, stats, report);
        }

        stats.WrapperBytes += value.Length - groupBytes.Length - childBytes;
    }

    /// <summary>
    /// The key range boundaries of any column, splitting the space evenly over the first two bytes of
    /// the key.
    /// </summary>
    /// <remarks>
    /// Every column is keyed path-major — a leaf blob by its stem, a trie node by its path then its
    /// depth (see <see cref="TrieNodeKey.WriteTo"/>) — and a stem is a hash, so an even split of that
    /// prefix gives ranges holding comparable numbers of entries. A trie node column covers only its
    /// own zone, so the ranges outside it are empty and cost no more than the seek that finds them so.
    /// </remarks>
    private static byte[][] PrefixBounds(int rangeCount, int keyLength)
    {
        byte[][] bounds = new byte[rangeCount + 1][];
        for (int range = 0; range < rangeCount; range++)
        {
            byte[] bound = new byte[keyLength];
            BinaryPrimitives.WriteUInt16BigEndian(bound, (ushort)((long)range * PrefixSpace / rangeCount));
            bounds[range] = bound;
        }

        bounds[rangeCount] = MaxKey(keyLength);
        return bounds;
    }

    /// <summary>
    /// The exclusive upper bound of the last range: one byte longer than the keys it must sit above, so
    /// that it also excludes none of them — an all-<c>0xFF</c> key of <paramref name="keyLength"/> is a
    /// prefix of it, and so sorts below it.
    /// </summary>
    private static byte[] MaxKey(int keyLength)
    {
        byte[] max = new byte[keyLength + 1];
        max.AsSpan().Fill(0xFF);
        return max;
    }

    private static void ScanChain(ReadOnlySpan<byte> entry, int startDepth, PbtScanReport.TrieNodeStats stats)
    {
        PbtNodeChain chain = PbtNodeChain.Decode(entry, startDepth);
        int span = chain.TargetDepth - startDepth;

        stats.ChainCount++;
        stats.ChainBytes += entry.Length;
        stats.ChainsByStartDepth[startDepth]++;
        stats.ChainsBySpan[span]++;

        // the run's own node is the chain blob and the target's is the group it lands on, so the levels
        // strictly between them are the ones the collapse leaves without an entry
        stats.ChainSkippedNodes += span - 1;

        // an every-level spine would spend one group per LevelsPerGroup levels, each storing a hash at
        // every level of the path but its root — which the parent caches — so LevelsPerGroup entries
        int groupsSpanned = span / PbtLayout.TrieNodeGroupLevelsPerGroup;
        stats.ChainEntriesAvoided += groupsSpanned * PbtLayout.TrieNodeGroupLevelsPerGroup - PbtScanReport.ChainStoredHashes;
        stats.ChainGroupBlobsAvoided += groupsSpanned;
    }

    /// <summary>
    /// Counts one group and the runs its boundary slots hold, each at the depth it starts at — one
    /// group below the group holding it.
    /// </summary>
    /// <remarks>
    /// The root group belongs to no one zone, but each of its boundary slots does — a slot of the root
    /// <em>is</em> the zone nibble — so a run it holds is counted where a key of its own would have put
    /// it, rather than beside the root.
    /// </remarks>
    private static void ScanGroup(
        in PbtTrieNodeGroup group, int bytes, int depth, PbtScanReport.TrieNodeStats stats, PbtScanReport report)
    {
        // A run's entry is the run's, not the group's, as a wrapped child's blob is the child's: the two
        // share an encoding, and the byte totals still say what each of them costs.
        int chainBytes = 0;
        for (int slot = 0; slot < PbtLayout.TrieNodeGroupBoundarySlots; slot++)
        {
            int position = PbtLayout.TrieNodeGroupBoundarySlotPosition(slot);
            if (group.KindAt(position) != PbtTrieNodeGroup.NodeKind.Chain) continue;

            ReadOnlySpan<byte> chain = group[position].ChainData;
            chainBytes += chain.Length;
            ScanChain(chain, depth + PbtLayout.TrieNodeGroupLevelsPerGroup, depth == 0 ? report[ZonePartition(slot)] : stats);
        }

        stats.GroupCount++;
        stats.GroupBytes += bytes - chainBytes;
        stats.GroupsByDepth[depth]++;
        stats.GroupBytesByDepth[depth] += bytes - chainBytes;
        if (group.Format == PbtGroupFormat.Interleaved) stats.InterleavedGroupCount++;
        if (depth == 0) report.RootSubtreeStemCount = group.Stats.StemCount;

        WalkPosition(group, PbtLayout.TrieNodeGroupRootPosition, PbtLayout.TrieNodeGroupBoundarySlots, depth, stats);
    }

    /// <summary>The partition of <paramref name="zone"/>, the leading nibble of a stem, as the leaf columns split them.</summary>
    private static PbtTreePartition ZonePartition(int zone) => zone switch
    {
        0x0 => PbtTreePartition.Account,
        0x1 => PbtTreePartition.Code,
        _ => PbtTreePartition.Storage,
    };

    /// <summary>
    /// Walks the tile in post-order from <paramref name="position"/>, counting the stems it holds and
    /// the internal nodes an interleaved encoding leaves unstored; returns whether the subtree is
    /// occupied at all.
    /// </summary>
    /// <remarks>
    /// A position covering <paramref name="width"/> boundary slots has its children at
    /// <c>position - width</c> and <c>position - 1</c>, each covering half as many, so the recursion
    /// tracks the group-relative level in <paramref name="depth"/> without any position-to-level table.
    /// The occupancy it returns is what the skipped-level count needs:
    /// <see cref="PbtTrieNodeGroup.KindAt"/> reports what the <em>encoding</em> holds, so an internal
    /// node at a skipped level reads absent though the trie has one, and only its subtree says whether
    /// it is there.
    /// </remarks>
    private static bool WalkPosition(in PbtTrieNodeGroup group, int position, int width, int depth, PbtScanReport.TrieNodeStats stats)
    {
        PbtTrieNodeGroup.NodeKind kind = group.KindAt(position);
        if (kind == PbtTrieNodeGroup.NodeKind.Stem)
        {
            // a stem is stored wherever it lands, skipped level or not, so it terminates the walk here
            stats.StemCount++;
            stats.StemsByDepth[depth]++;
            return true;
        }

        // a boundary slot roots no position of this tile; it holds the cached root of the blob below it,
        // or the run hanging from it, and nothing at all means an absent subtree
        if (width == 1) return kind != PbtTrieNodeGroup.NodeKind.Absent;

        int childWidth = width / 2;
        bool left = WalkPosition(group, position - width, childWidth, depth + 1, stats);
        bool right = WalkPosition(group, position - 1, childWidth, depth + 1, stats);
        if (!left && !right) return false;

        if (PbtLayout.TrieNodeGroupIsSkippedPosition(group.Format, position)) stats.InterleaveSkippedNodes++;
        return true;
    }

    /// <remarks>
    /// A blob's entries are its present leaves and its branching internals in one undifferentiated
    /// post-order run, so the leaf count comes from the footer bitmap and the internals are whatever
    /// the run holds beyond them.
    /// </remarks>
    private static void ScanLeafBlob(ReadOnlySpan<byte> blob, PbtScanReport.LeafColumnStats stats)
    {
        TwoLevelBitmapReader reader = TwoLevelBitmapReader.FromBlob(blob, out ReadOnlySpan<byte> entries);
        Span<byte> bitmap = stackalloc byte[TwoLevelBitmapReader.BitmapLength];
        reader.ExpandTo(bitmap);

        int leaves = 0;
        for (int i = 0; i < bitmap.Length; i++) leaves += BitOperations.PopCount(bitmap[i]);

        stats.BlobCount++;
        stats.Bytes += blob.Length;
        stats.LeafCount += leaves;
        stats.IntermediateNodeCount += entries.Length / StemLeafBlob.ValueLength - leaves;
        stats.BlobsByLeafCount[leaves]++;
        if (TwoLevelBitmapReader.IsLegacy(blob)) stats.LegacyBlobCount++;
    }

    /// <remarks>
    /// Ranges are claimed in turn and there are far more of them than workers, so the fraction finished
    /// is a fair completion estimate — fairer than the entry count, whose target is RocksDB's own
    /// estimate. The count still drives <see cref="ProgressLogger.CurrentValue"/> only so that it moves
    /// every tick: the logger drops a repeated line unless it changes, which a long range would stall.
    /// </remarks>
    private ProgressLogger CreateProgressLogger(PbtColumns columnName, IDb column, Func<float> completion)
    {
        ProgressLogger progress = new($"PBT scan {columnName}", logManager);
        progress.SetFormat(logger =>
        {
            float percentage = Math.Clamp(completion(), 0, 1);
            return $"PBT scan {columnName,-14} {percentage.ToString("P2", CultureInfo.InvariantCulture),8} {Progress.GetMeter(percentage, 1)} | " +
                $"{logger.CurrentValue,15:N0} entries ({logger.CurrentPerSecond,10:N0}/s) of ~{logger.TargetValue,15:N0}";
        });
        progress.Reset(0, (ulong)Math.Max(column.EstimatedCount, 0));
        return progress;
    }
}

/// <summary>What a <see cref="PbtScanner"/> sweep counted, grouped by <see cref="PbtTreePartition"/>.</summary>
public sealed class PbtScanReport
{
    /// <summary>Depths a histogram covers; a trie node key's depth is one byte.</summary>
    private const int DepthSlots = 256;

    /// <summary>Leaves one stem can hold, plus the unused zero bucket — a stored blob has at least one.</summary>
    private const int LeafSlots = 257;

    /// <summary>Hashes a chain stores in place of the spine it replaces: its target's root and its own node hash.</summary>
    internal const int ChainStoredHashes = 2;

    private static readonly PbtTreePartition[] Partitions = Enum.GetValues<PbtTreePartition>();

    private readonly TrieNodeStats[] _byPartition = CreatePartitions();

    private static TrieNodeStats[] CreatePartitions()
    {
        TrieNodeStats[] partitions = new TrieNodeStats[Partitions.Length];
        for (int i = 0; i < partitions.Length; i++) partitions[i] = new TrieNodeStats();
        return partitions;
    }

    public TrieNodeStats this[PbtTreePartition partition] => _byPartition[(int)partition];

    /// <summary>The root group, which spans every zone and so is counted apart from all of them.</summary>
    public TrieNodeStats Root => this[PbtTreePartition.Root];

    public TrieNodeStats AccountNodes => this[PbtTreePartition.Account];
    public TrieNodeStats CodeNodes => this[PbtTreePartition.Code];
    public TrieNodeStats StorageNodes => this[PbtTreePartition.Storage];

    public LeafColumnStats AccountLeaves { get; } = new();
    public LeafColumnStats CodeLeaves { get; } = new();
    public LeafColumnStats StorageLeaves { get; } = new();

    /// <summary>
    /// The stem count the root node carries for its whole subtree, which every stored node caches;
    /// it must equal <see cref="StemCount"/>, so the two disagreeing means the sweep or the stored
    /// statistics are wrong.
    /// </summary>
    public long RootSubtreeStemCount { get; internal set; }

    public long GroupCount => Sum(static stats => stats.GroupCount);
    public long ChainCount => Sum(static stats => stats.ChainCount);
    public long StemCount => Sum(static stats => stats.StemCount);
    public long TrieNodeBytes => Sum(static stats => stats.GroupBytes + stats.ChainBytes + stats.WrapperBytes);
    public long WrapperCount => Sum(static stats => stats.WrapperCount);
    public long WrappedChildCount => Sum(static stats => stats.WrappedChildCount);
    public long InterleaveSkippedNodes => Sum(static stats => stats.InterleaveSkippedNodes);
    public long ChainSkippedNodes => Sum(static stats => stats.ChainSkippedNodes);
    public long ChainEntriesAvoided => Sum(static stats => stats.ChainEntriesAvoided);
    public long ChainGroupBlobsAvoided => Sum(static stats => stats.ChainGroupBlobsAvoided);

    public bool StemCountAgrees => RootSubtreeStemCount == StemCount;

    /// <summary>Adds everything <paramref name="other"/> counted into this report.</summary>
    /// <remarks>
    /// Only the worker that scanned the root's range ever records a
    /// <see cref="RootSubtreeStemCount"/>, so the greatest is it.
    /// </remarks>
    internal void MergeFrom(PbtScanReport other)
    {
        for (int i = 0; i < _byPartition.Length; i++) _byPartition[i].MergeFrom(other._byPartition[i]);

        AccountLeaves.MergeFrom(other.AccountLeaves);
        CodeLeaves.MergeFrom(other.CodeLeaves);
        StorageLeaves.MergeFrom(other.StorageLeaves);

        RootSubtreeStemCount = Math.Max(RootSubtreeStemCount, other.RootSubtreeStemCount);
    }

    private static void AddInto(long[] into, long[] from)
    {
        for (int i = 0; i < into.Length; i++) into[i] += from[i];
    }

    private long Sum(Func<TrieNodeStats, long> selector)
    {
        long total = 0;
        foreach (TrieNodeStats stats in _byPartition) total += selector(stats);
        return total;
    }

    /// <summary>The trie nodes of one zone subtree.</summary>
    public sealed class TrieNodeStats
    {
        public long GroupCount { get; internal set; }
        public long InterleavedGroupCount { get; internal set; }
        public long ChainCount { get; internal set; }
        public long StemCount { get; internal set; }
        public long GroupBytes { get; internal set; }
        public long ChainBytes { get; internal set; }

        /// <summary>Blobs holding their children's blobs alongside their own group.</summary>
        public long WrapperCount { get; internal set; }

        /// <summary>Children so held — the lookups, and the keys, that the wrapping saves.</summary>
        public long WrappedChildCount { get; internal set; }

        /// <summary>The offset tables and counts those blobs spend to hold them.</summary>
        public long WrapperBytes { get; internal set; }

        public long[] GroupsByDepth { get; } = new long[DepthSlots];
        public long[] GroupBytesByDepth { get; } = new long[DepthSlots];
        public long[] StemsByDepth { get; } = new long[DepthSlots];

        /// <summary>Wrappers by the depth of the group they hold, which is where their key sits.</summary>
        public long[] WrappersByDepth { get; } = new long[DepthSlots];

        /// <summary>
        /// The whole stored length of those wrappers — their own group, every child they hold and the
        /// framing between them — so that the average is the value size the store sees at that depth.
        /// </summary>
        /// <remarks>
        /// Not <see cref="GroupBytesByDepth"/> restricted to wrappers: that counts each group where it
        /// sits, so a wrapper's children land a group lower and its framing lands nowhere.
        /// </remarks>
        public long[] WrapperBlobBytesByDepth { get; } = new long[DepthSlots];

        /// <summary>
        /// Wrapped children by the depth of the wrapper holding them, so that the two histograms read
        /// as one table; a child itself sits <see cref="PbtLayout.TrieNodeGroupLevelsPerGroup"/> levels below.
        /// </summary>
        public long[] WrappedChildrenByDepth { get; } = new long[DepthSlots];

        public long[] ChainsByStartDepth { get; } = new long[DepthSlots];
        public long[] ChainsBySpan { get; } = new long[DepthSlots];

        /// <summary>Internal nodes the interleaved encoding leaves unstored; also the entries it saves, one hash each.</summary>
        public long InterleaveSkippedNodes { get; internal set; }

        /// <summary>Trie levels the chains collapsed, each a real single-child node with no entry of its own.</summary>
        public long ChainSkippedNodes { get; internal set; }

        /// <summary>Hash entries the chains saved against an every-level spine of groups.</summary>
        public long ChainEntriesAvoided { get; internal set; }

        /// <summary>Group blobs the chains replaced, holding no blob of their own to set against them.</summary>
        public long ChainGroupBlobsAvoided { get; internal set; }

        public bool IsEmpty => GroupCount == 0 && ChainCount == 0;

        internal void MergeFrom(TrieNodeStats other)
        {
            GroupCount += other.GroupCount;
            InterleavedGroupCount += other.InterleavedGroupCount;
            ChainCount += other.ChainCount;
            StemCount += other.StemCount;
            GroupBytes += other.GroupBytes;
            ChainBytes += other.ChainBytes;
            WrapperCount += other.WrapperCount;
            WrappedChildCount += other.WrappedChildCount;
            WrapperBytes += other.WrapperBytes;
            InterleaveSkippedNodes += other.InterleaveSkippedNodes;
            ChainSkippedNodes += other.ChainSkippedNodes;
            ChainEntriesAvoided += other.ChainEntriesAvoided;
            ChainGroupBlobsAvoided += other.ChainGroupBlobsAvoided;

            AddInto(GroupsByDepth, other.GroupsByDepth);
            AddInto(GroupBytesByDepth, other.GroupBytesByDepth);
            AddInto(StemsByDepth, other.StemsByDepth);
            AddInto(WrappersByDepth, other.WrappersByDepth);
            AddInto(WrapperBlobBytesByDepth, other.WrapperBlobBytesByDepth);
            AddInto(WrappedChildrenByDepth, other.WrappedChildrenByDepth);
            AddInto(ChainsByStartDepth, other.ChainsByStartDepth);
            AddInto(ChainsBySpan, other.ChainsBySpan);
        }
    }

    /// <summary>What one leaf-blob column holds.</summary>
    public sealed class LeafColumnStats
    {
        public long BlobCount { get; internal set; }
        public long Bytes { get; internal set; }

        /// <summary>Present leaves across every blob: the stored slots, code chunks or header fields.</summary>
        public long LeafCount { get; internal set; }

        /// <summary>Branching internal nodes stored alongside those leaves.</summary>
        public long IntermediateNodeCount { get; internal set; }

        /// <summary>Blobs still in the legacy layout, which caches every single-child internal too.</summary>
        public long LegacyBlobCount { get; internal set; }

        /// <summary>Blobs by how many of the stem's 256 leaves they hold, indexed by that count.</summary>
        public long[] BlobsByLeafCount { get; } = new long[LeafSlots];

        internal void MergeFrom(LeafColumnStats other)
        {
            BlobCount += other.BlobCount;
            Bytes += other.Bytes;
            LeafCount += other.LeafCount;
            IntermediateNodeCount += other.IntermediateNodeCount;
            LegacyBlobCount += other.LegacyBlobCount;

            AddInto(BlobsByLeafCount, other.BlobsByLeafCount);
        }
    }

    public string Format()
    {
        StringBuilder report = new();
        report.AppendLine();
        report.AppendLine("=== PBT scan ===");
        report.AppendLine();

        report.AppendLine($"Totals: {GroupCount + ChainCount:N0} trie nodes ({TrieNodeBytes:N0} bytes), {GroupCount:N0} groups, {ChainCount:N0} chains, {StemCount:N0} stems");
        report.AppendLine($"Stored in {GroupCount - WrappedChildCount:N0} blobs: every chain rides in the group above it, and {WrapperCount:N0} groups wrap {WrappedChildCount:N0} more");
        report.AppendLine($"Root records {RootSubtreeStemCount:N0} stems for its subtree ({(StemCountAgrees ? "agrees" : "MISMATCH")})");
        report.AppendLine();

        report.AppendLine("Intermediate nodes with no stored entry");
        report.AppendLine($"  {"partition",-10}  {"interleaved levels",20}  {"chain levels",14}  {"chain entries saved",21}");
        foreach (PbtTreePartition partition in Partitions)
        {
            TrieNodeStats stats = this[partition];
            if (stats.IsEmpty) continue;
            report.AppendLine($"  {partition,-10}  {stats.InterleaveSkippedNodes,20:N0}  {stats.ChainSkippedNodes,14:N0}  {stats.ChainEntriesAvoided,21:N0}");
        }

        report.AppendLine($"  {"TOTAL",-10}  {InterleaveSkippedNodes,20:N0}  {ChainSkippedNodes,14:N0}  {ChainEntriesAvoided,21:N0}");
        report.AppendLine($"  (an interleaved level saves exactly one entry each; chains also replaced {ChainGroupBlobsAvoided:N0} group blobs)");
        report.AppendLine();

        foreach (PbtTreePartition partition in Partitions) AppendPartition(report, partition);

        AppendLeafColumn(report, "Account leaf blobs (zone 0x0)", AccountLeaves);
        AppendLeafColumn(report, "Code leaf blobs (zone 0x1)", CodeLeaves);
        AppendLeafColumn(report, "Storage leaf blobs (zones 0x8-0xF)", StorageLeaves);

        return report.ToString();
    }

    private void AppendPartition(StringBuilder report, PbtTreePartition partition)
    {
        TrieNodeStats stats = this[partition];
        if (stats.IsEmpty) return;

        report.AppendLine($"--- {partition} ---");
        report.AppendLine($"  {stats.GroupCount:N0} groups ({stats.InterleavedGroupCount:N0} interleaved, {stats.GroupBytes:N0} bytes), {stats.ChainCount:N0} chains ({stats.ChainBytes:N0} bytes), {stats.StemCount:N0} stems");
        if (stats.WrapperCount != 0)
        {
            report.AppendLine($"  {stats.WrapperCount:N0} of those blobs also hold {stats.WrappedChildCount:N0} of those nodes, for {stats.WrapperBytes:N0} bytes of framing");
        }

        report.AppendLine();

        AppendDepthTable(report, "Trie node groups by depth", ("groups", "bytes", "avg size"), stats.GroupsByDepth, stats.GroupBytesByDepth);
        AppendDepthTable(report, "Wrappers by depth", ("wrappers", "children", "avg children"), stats.WrappersByDepth, stats.WrappedChildrenByDepth);
        AppendDepthTable(report, "Wrapper blobs by depth", ("wrappers", "bytes", "avg size"), stats.WrappersByDepth, stats.WrapperBlobBytesByDepth);
        AppendCountTable(report, "Stems by depth", "depth", "stems", stats.StemsByDepth);
        AppendCountTable(report, "Node chains by start depth", "depth", "chains", stats.ChainsByStartDepth);
        AppendCountTable(report, "Node chains by span", "levels", "chains", stats.ChainsBySpan);
    }

    /// <summary>A depth histogram of two totals, the second averaged over the first.</summary>
    private static void AppendDepthTable(StringBuilder report, string title, (string Count, string Total, string Average) columns, long[] counts, long[] totals)
    {
        if (!HasAny(counts)) return;

        report.AppendLine($"  {title}");
        report.AppendLine($"    {"depth",5}  {columns.Count,15}  {columns.Total,18}  {columns.Average,12}");
        for (int depth = 0; depth < counts.Length; depth++)
        {
            if (counts[depth] == 0) continue;
            report.AppendLine($"    {depth,5}  {counts[depth],15:N0}  {totals[depth],18:N0}  {(double)totals[depth] / counts[depth],12:N1}");
        }

        report.AppendLine();
    }

    private static void AppendCountTable(StringBuilder report, string title, string keyHeader, string unit, long[] counts)
    {
        if (!HasAny(counts)) return;

        report.AppendLine($"  {title}");
        report.AppendLine($"    {keyHeader,6}  {unit,15}");
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] != 0) report.AppendLine($"    {i,6}  {counts[i],15:N0}");
        }

        report.AppendLine();
    }

    private static bool HasAny(long[] counts)
    {
        foreach (long count in counts)
        {
            if (count != 0) return true;
        }

        return false;
    }

    private static void AppendLeafColumn(StringBuilder report, string title, LeafColumnStats stats)
    {
        report.AppendLine($"--- {title} ---");
        report.AppendLine($"  {stats.BlobCount:N0} blobs, {stats.Bytes:N0} bytes, {stats.LeafCount:N0} leaves, {stats.IntermediateNodeCount:N0} intermediate nodes, {stats.LegacyBlobCount:N0} legacy");
        if (stats.BlobCount == 0)
        {
            report.AppendLine();
            return;
        }

        report.AppendLine($"    {"leaves",6}  {"blobs",15}");
        for (int leaves = 0; leaves < stats.BlobsByLeafCount.Length; leaves++)
        {
            if (stats.BlobsByLeafCount[leaves] != 0) report.AppendLine($"    {leaves,6}  {stats.BlobsByLeafCount[leaves],15:N0}");
        }

        report.AppendLine();
    }
}
