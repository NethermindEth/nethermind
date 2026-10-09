// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Memory;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Image;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Steps;

/// <summary>Counts persisted flat records and the shape of their stored PBT node groups.</summary>
/// <remarks>This inventory does not verify hashes, references or reachability and performs no point reads.</remarks>
public sealed class PbtScanner(IColumnsDb<PbtColumns> db, IPbtConfig config, ILogManager logManager)
{
    private const int RangesPerWorker = 16;
    private const int ProgressPublishInterval = 100_000;

    /// <summary>Sweeps each active data column with independent parallel range readers.</summary>
    public async Task<PbtScanReport> Scan(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int workerCount = config.ScanTreeConcurrency > 0 ? config.ScanTreeConcurrency : Environment.ProcessorCount;
        int rangeCount = (int)Math.Min((long)workerCount * RangesPerWorker, PbtPrefixPartitions.PrefixSpace);
        PbtScanReport report = new();
        foreach (PbtColumns column in PbtScanReport.ScannedColumns)
            await ScanColumn(column, CreateBounds(column, rangeCount), report, workerCount, cancellationToken);
        return report;
    }

    private async Task ScanColumn(PbtColumns columnName, byte[][] bounds, PbtScanReport report, int workerCount, CancellationToken cancellationToken)
    {
        IDb column = db.GetColumnDb(columnName);
        if (column is not ISortedKeyValueStore sorted)
            throw new InvalidOperationException($"The PBT {columnName} column is a {column.GetType().Name}, which cannot be range scanned.");

        int rangeCount = bounds.Length - 1;
        workerCount = Math.Min(workerCount, rangeCount);
        int nextRange = -1, completedRanges = 0;
        long scanned = 0;
        using CancellationTokenSource workersCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken workerToken = workersCancellation.Token;
        PbtScanReport[] shards = new PbtScanReport[workerCount];
        Task[] workers = new Task[workerCount];
        string phase = $"PBT scan {columnName}";
        using ProgressReporter progress = PbtImageProgress.Start(phase, "entries", 0, logManager);
        progress.Logger.SetFormat(logger => PbtImageProgress.Format(phase, Volatile.Read(ref completedRanges) / (float)rangeCount, PbtImageProgress.Counted("entries", logger)));

        void ScanRanges(PbtScanReport shard)
        {
            long pending = 0;
            PbtScanReport.ColumnStats stats = shard[columnName];
            try
            {
                int range;
                while ((range = Interlocked.Increment(ref nextRange)) < rangeCount)
                {
                    workerToken.ThrowIfCancellationRequested();
                    using ISortedView view = sorted.GetViewBetween(bounds[range], bounds[range + 1], ReadFlags.HintCacheMiss);
                    while (view.MoveNext())
                    {
                        workerToken.ThrowIfCancellationRequested();
                        stats.RecordCount++;
                        stats.KeyBytes += view.CurrentKey.Length;
                        stats.ValueBytes += view.CurrentValue.Length;
                        if (columnName == PbtColumns.Metadata)
                            ScanGroup(new PbtNodePath([], 0), view.CurrentValue, (PbtScanReport.NodeGroupStats)stats);
                        else if (IsNodeGroupColumn(columnName))
                        {
                            PbtStorageNodePath groupPath = PbtNodeGroupKey.Decode(view.CurrentKey);
                            // A top group's bytes belong to its physical column; its shape belongs to its partition.
                            PbtScanReport.NodeGroupStats shape = columnName == PbtColumns.TopNodeGroups
                                ? (PbtScanReport.NodeGroupStats)shard[PbtRocksDbPersistence.PartitionColumn(groupPath)]
                                : (PbtScanReport.NodeGroupStats)stats;
                            ScanGroup(groupPath, view.CurrentValue, shape);
                        }
                        if (++pending == ProgressPublishInterval)
                        {
                            progress.Update((ulong)Interlocked.Add(ref scanned, pending));
                            pending = 0;
                        }
                    }
                    Interlocked.Increment(ref completedRanges);
                }
            }
            catch
            {
                workersCancellation.Cancel();
                throw;
            }
            finally
            {
                progress.Update((ulong)Interlocked.Add(ref scanned, pending));
            }
        }

        for (int worker = 0; worker < workerCount; worker++)
        {
            PbtScanReport shard = shards[worker] = new();
            workers[worker] = Task.Run(() => ScanRanges(shard), CancellationToken.None);
        }
        await Task.WhenAll(workers);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (PbtScanReport shard in shards) report.MergeFrom(shard);
    }

    private static bool IsNodeGroupColumn(PbtColumns column) =>
        column is PbtColumns.AccountNodeGroups or PbtColumns.CodeNodeGroups or PbtColumns.StorageNodeGroups or PbtColumns.TopNodeGroups;

    private static void ScanGroup<TPath>(TPath groupPath, ReadOnlySpan<byte> value, PbtScanReport.NodeGroupStats stats) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtNodeGroupCodec.ValidateNodes(groupPath, value);
        using GroupFrameReader<PbtVariableTreeKey, PbtStorageNodePath> reader = new(RefCountingMemory.Wrapping(value.ToArray()), groupPath.BitDepth);
        stats.GroupsByDepth[groupPath.BitDepth]++;
        stats.PayloadBytesByDepth[groupPath.BitDepth] += value.Length;
        stats.GroupsByOccupancy[BitOperations.PopCount(reader.StoredPositions)]++;
        for (uint stored = reader.StoredPositions; stored != 0; stored &= stored - 1)
        {
            int position = BitOperations.TrailingZeroCount(stored);
            ReadOnlySpan<byte> encoding = reader.GetEncoding(position).Span;
            stats.NodeCount++;
            stats.NodeEncodingBytes += encoding.Length;
            if (PbtNodeCodec.IsLeaf(encoding)) stats.LeafCount++;
            else
            {
                PbtBranchReader node = PbtBranchReader.FromValidated(encoding);
                stats.BranchCount++;
                if (!node.LeftKeyPostfix.IsEmpty) stats.LeafCount++;
                if (!node.RightKeyPostfix.IsEmpty) stats.LeafCount++;
            }
            stats.NodesByDepth[groupPath.BitDepth + PbtFourLevelGroupGeometry.LocalPathOf(position).Length]++;
        }
        if (reader.DescendantMask != 0) stats.GroupsWithDescendants++;
    }

    private static byte[][] CreateBounds(PbtColumns column, int rangeCount)
    {
        if (column == PbtColumns.Metadata)
        {
            byte[] rootKey = PbtRocksDbPersistence.RootNodeGroupKey.ToArray();
            byte[] pastRootKey = new byte[rootKey.Length + 1];
            rootKey.CopyTo(pastRootKey, 0);
            return [rootKey, pastRootKey];
        }

        List<byte[]> bounds = [[]];
        if (IsNodeGroupColumn(column) && column != PbtColumns.TopNodeGroups)
        {
            byte zone = column switch
            {
                PbtColumns.AccountNodeGroups => Eip8297KeyDerivation.AccountZone,
                PbtColumns.CodeNodeGroups => Eip8297KeyDerivation.CodeZone,
                _ => Eip8297KeyDerivation.StorageZone,
            };
            for (int partition = 0; partition < rangeCount; partition++)
            {
                byte[] boundary = new byte[3];
                boundary[0] = zone;
                BinaryPrimitives.WriteUInt16BigEndian(boundary.AsSpan(1), (ushort)PbtPrefixPartitions.Start(partition, rangeCount));
                bounds.Add(boundary);
            }
        }
        else
        {
            for (int partition = 1; partition < rangeCount; partition++)
            {
                byte[] boundary = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(boundary, (ushort)PbtPrefixPartitions.Start(partition, rangeCount));
                bounds.Add(boundary);
            }
        }
        bounds.Add(PbtColumnSweep.PastEveryKey());
        return bounds.ToArray();
    }
}

/// <summary>Inventory of persisted PBT records, without hash or reachability verification.</summary>
public sealed class PbtScanReport
{
    internal static readonly PbtColumns[] ScannedColumns = [PbtColumns.Accounts, PbtColumns.Storages, PbtColumns.Codes, PbtColumns.TopNodeGroups,
        PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups, PbtColumns.StorageNodeGroups, PbtColumns.Metadata];

    /// <summary>Stored whole-account records.</summary>
    public ColumnStats Accounts { get; } = new();
    /// <summary>Stored storage-word records.</summary>
    public ColumnStats Storages { get; } = new();
    /// <summary>Stored whole-bytecode records, including unreferenced code.</summary>
    public ColumnStats Codes { get; } = new();
    /// <summary>Stored account and shared account/code node groups, with their locally decoded shape.</summary>
    public NodeGroupStats AccountNodeGroups { get; } = new();
    /// <summary>Stored code node groups, with their locally decoded shape.</summary>
    public NodeGroupStats CodeNodeGroups { get; } = new();
    /// <summary>Stored storage node groups, with their locally decoded shape.</summary>
    public NodeGroupStats StorageNodeGroups { get; } = new();
    /// <summary>Stored top node groups; their shape is counted under the partition each group belongs to.</summary>
    public NodeGroupStats TopNodeGroups { get; } = new();
    /// <summary>The root node group stored in metadata, excluding other metadata records.</summary>
    public NodeGroupStats MetadataRoot { get; } = new();
    /// <summary>Aggregate stored node groups across partition columns and the metadata root, with their locally decoded shape.</summary>
    public NodeGroupStats NodeGroups { get; } = new();

    /// <summary>Gets per-column statistics, with metadata limited to the root node group.</summary>
    public ColumnStats this[PbtColumns column] => column switch
    {
        PbtColumns.Accounts => Accounts,
        PbtColumns.Storages => Storages,
        PbtColumns.Codes => Codes,
        PbtColumns.AccountNodeGroups => AccountNodeGroups,
        PbtColumns.CodeNodeGroups => CodeNodeGroups,
        PbtColumns.StorageNodeGroups => StorageNodeGroups,
        PbtColumns.TopNodeGroups => TopNodeGroups,
        PbtColumns.Metadata => MetadataRoot,
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    internal void MergeFrom(PbtScanReport other)
    {
        Accounts.MergeFrom(other.Accounts);
        Storages.MergeFrom(other.Storages);
        Codes.MergeFrom(other.Codes);
        AccountNodeGroups.MergeFrom(other.AccountNodeGroups);
        CodeNodeGroups.MergeFrom(other.CodeNodeGroups);
        StorageNodeGroups.MergeFrom(other.StorageNodeGroups);
        TopNodeGroups.MergeFrom(other.TopNodeGroups);
        MetadataRoot.MergeFrom(other.MetadataRoot);
        NodeGroups.MergeFrom(other.TopNodeGroups);
        NodeGroups.MergeFrom(other.AccountNodeGroups);
        NodeGroups.MergeFrom(other.CodeNodeGroups);
        NodeGroups.MergeFrom(other.StorageNodeGroups);
        NodeGroups.MergeFrom(other.MetadataRoot);
    }

    /// <summary>Formats stored sizes and node-group histograms, aggregated and per partition column.</summary>
    public string Format()
    {
        StringBuilder report = new();
        report.AppendLine();
        report.AppendLine("=== PBT scan: persisted inventory (not hash or reachability verification) ===");
        report.AppendLine($"  {"column",-20} {"records",15} {"key bytes",18} {"value bytes",18} {"total bytes",18} {"avg bytes",12}");
        foreach (PbtColumns column in ScannedColumns)
        {
            ColumnStats stats = this[column];
            string label = column == PbtColumns.Metadata ? "Metadata (root only)" : column.ToString();
            report.AppendLine($"  {label,-20} {stats.RecordCount,15:N0} {stats.KeyBytes,18:N0} {stats.ValueBytes,18:N0} {stats.TotalBytes,18:N0} {stats.AverageRecordBytes,12:N1}");
        }
        AppendNodeGroupShape(report, "all partitions", NodeGroups);
        AppendNodeGroupShape(report, nameof(PbtColumns.AccountNodeGroups), AccountNodeGroups);
        AppendNodeGroupShape(report, nameof(PbtColumns.CodeNodeGroups), CodeNodeGroups);
        AppendNodeGroupShape(report, nameof(PbtColumns.StorageNodeGroups), StorageNodeGroups);
        return report.ToString();
    }

    private static void AppendNodeGroupShape(StringBuilder report, string label, NodeGroupStats stats)
    {
        report.AppendLine($"Contained nodes ({label}): {stats.NodeCount:N0} ({stats.LeafCount:N0} leaves, {stats.BranchCount:N0} branches), {stats.NodeEncodingBytes:N0} encoding bytes (excluding group keys and footers)");
        report.AppendLine($"Node groups and contained nodes by bit depth ({label})");
        report.AppendLine($"  {"depth",6} {"groups",15} {"payload bytes",18} {"nodes",15}");
        for (int depth = 0; depth < stats.NodesByDepth.Length; depth++)
            if (stats.GroupsByDepth[depth] != 0 || stats.NodesByDepth[depth] != 0)
                report.AppendLine($"  {depth,6} {stats.GroupsByDepth[depth],15:N0} {stats.PayloadBytesByDepth[depth],18:N0} {stats.NodesByDepth[depth],15:N0}");
        report.AppendLine($"Node-group occupancy ({label})");
        report.AppendLine($"  {"nodes",6} {"groups",15}");
        for (int occupancy = 0; occupancy < stats.GroupsByOccupancy.Length; occupancy++)
            if (stats.GroupsByOccupancy[occupancy] != 0)
                report.AppendLine($"  {occupancy,6} {stats.GroupsByOccupancy[occupancy],15:N0}");
        report.AppendLine($"Descendant sizes ({label}): {stats.GroupsWithDescendants:N0} groups");
    }

    /// <summary>Actual persisted row sizes, with each key and value counted once.</summary>
    public class ColumnStats
    {
        /// <summary>Number of stored records.</summary>
        public long RecordCount { get; internal set; }
        /// <summary>Total stored key bytes.</summary>
        public long KeyBytes { get; internal set; }
        /// <summary>Total stored value bytes.</summary>
        public long ValueBytes { get; internal set; }
        /// <summary>Total stored key and value bytes.</summary>
        public long TotalBytes => KeyBytes + ValueBytes;
        /// <summary>Mean key plus value size, or zero for an empty column.</summary>
        public double AverageRecordBytes => RecordCount == 0 ? 0 : (double)TotalBytes / RecordCount;

        internal void MergeFrom(ColumnStats other)
        {
            RecordCount += other.RecordCount;
            KeyBytes += other.KeyBytes;
            ValueBytes += other.ValueBytes;
        }
    }

    /// <summary>Persisted group sizes and locally decoded node shape.</summary>
    public sealed class NodeGroupStats : ColumnStats
    {
        /// <summary>Number of contained nodes.</summary>
        public long NodeCount { get; internal set; }
        /// <summary>Number of contained leaf nodes.</summary>
        public long LeafCount { get; internal set; }
        /// <summary>Number of contained branch nodes.</summary>
        public long BranchCount { get; internal set; }
        /// <summary>Contained encoding bytes, excluding group keys and footers.</summary>
        public long NodeEncodingBytes { get; internal set; }
        /// <summary>Stored groups by boundary bit depth.</summary>
        public long[] GroupsByDepth { get; } = new long[PbtFourLevelGroupGeometry.MaxPathDepth + 1];
        /// <summary>Whole group payload bytes by boundary bit depth.</summary>
        public long[] PayloadBytesByDepth { get; } = new long[PbtFourLevelGroupGeometry.MaxPathDepth + 1];
        /// <summary>Contained nodes by their position's bit depth.</summary>
        public long[] NodesByDepth { get; } = new long[PbtFourLevelGroupGeometry.MaxPathDepth + 1];
        /// <summary>Stored groups by the number of nodes they contain.</summary>
        public long[] GroupsByOccupancy { get; } = new long[PbtFourLevelGroupGeometry.PositionCount + 1];
        /// <summary>Stored groups with at least one nonzero descendant size.</summary>
        public long GroupsWithDescendants { get; internal set; }

        internal void MergeFrom(NodeGroupStats other)
        {
            base.MergeFrom(other);
            NodeCount += other.NodeCount;
            LeafCount += other.LeafCount;
            BranchCount += other.BranchCount;
            NodeEncodingBytes += other.NodeEncodingBytes;
            AddInto(GroupsByDepth, other.GroupsByDepth);
            AddInto(PayloadBytesByDepth, other.PayloadBytesByDepth);
            AddInto(NodesByDepth, other.NodesByDepth);
            AddInto(GroupsByOccupancy, other.GroupsByOccupancy);
            GroupsWithDescendants += other.GroupsWithDescendants;
        }

        private static void AddInto(long[] target, long[] source)
        {
            for (int index = 0; index < target.Length; index++) target[index] += source[index];
        }
    }
}
