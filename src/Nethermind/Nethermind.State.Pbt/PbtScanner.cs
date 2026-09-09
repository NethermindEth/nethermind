// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Counts persisted flat records and the shape of their stored PBT node groups.</summary>
/// <remarks>This inventory does not verify hashes, references or reachability and performs no point reads.</remarks>
public sealed class PbtScanner(IColumnsDb<PbtColumns> db, IPbtConfig config, ILogManager logManager)
{
    private const int RangesPerWorker = 16;
    private const int PrefixSpace = 1 << 16;
    private const int ProgressPublishInterval = 100_000;
    private static readonly TimeSpan ProgressLogInterval = TimeSpan.FromSeconds(5);
    private static readonly int[] PositionDepths = CreatePositionDepths();
    private readonly ILogger _logger = logManager.GetClassLogger<PbtScanner>();

    /// <summary>Sweeps each active data column with independent parallel range readers.</summary>
    public async Task<PbtScanReport> Scan(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int workerCount = config.ScanTreeConcurrency > 0 ? config.ScanTreeConcurrency : Environment.ProcessorCount;
        int rangeCount = (int)Math.Min((long)workerCount * RangesPerWorker, PrefixSpace);
        PbtScanReport report = new();
        foreach (PbtColumns column in new[] { PbtColumns.Accounts, PbtColumns.Storages, PbtColumns.Codes, PbtColumns.NodeGroups })
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
        using CancellationTokenSource loggingCancellation = new();
        CancellationToken workerToken = workersCancellation.Token;
        PbtScanReport[] shards = new PbtScanReport[workerCount];
        Task[] workers = new Task[workerCount];
        Stopwatch elapsed = Stopwatch.StartNew();
        long previousCount = 0;
        double previousSeconds = 0;

        void LogProgress(bool completed)
        {
            long count = Interlocked.Read(ref scanned);
            double seconds = elapsed.Elapsed.TotalSeconds;
            double interval = seconds - previousSeconds;
            double rate = interval > 0 ? (count - previousCount) / interval : 0;
            if (_logger.IsInfo)
                _logger.Info($"PBT scan {columnName}: {count:N0} entries, elapsed {seconds:N1}s, {rate:N0} entries/s, approximately {(double)Volatile.Read(ref completedRanges) / rangeCount:P1} of ranges{(completed ? " (completed)" : "")}.");
            previousCount = count;
            previousSeconds = seconds;
        }

        async Task LogPeriodically()
        {
            try
            {
                using PeriodicTimer timer = new(ProgressLogInterval);
                while (await timer.WaitForNextTickAsync(loggingCancellation.Token)) LogProgress(false);
            }
            catch (OperationCanceledException) when (loggingCancellation.IsCancellationRequested) { }
            catch
            {
                workersCancellation.Cancel();
                throw;
            }
        }

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
                        if (columnName == PbtColumns.NodeGroups) ScanGroup(view.CurrentKey, view.CurrentValue, shard.NodeGroups);
                        if (++pending == ProgressPublishInterval)
                        {
                            Interlocked.Add(ref scanned, pending);
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
                Interlocked.Add(ref scanned, pending);
            }
        }

        Task logging = LogPeriodically();
        for (int worker = 0; worker < workerCount; worker++)
        {
            PbtScanReport shard = shards[worker] = new();
            workers[worker] = Task.Run(() => ScanRanges(shard), CancellationToken.None);
        }
        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            await loggingCancellation.CancelAsync();
            await logging;
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (PbtScanReport shard in shards) report.MergeFrom(shard);
        LogProgress(true);
    }

    private static void ScanGroup(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, PbtScanReport.NodeGroupStats stats)
    {
        IPbtNodePath groupPath = PbtPathOperations.Decode(key);
        PbtNodeGroupReader reader = new(groupPath, value);
        stats.GroupsByDepth[groupPath.BitDepth]++;
        stats.PayloadBytesByDepth[groupPath.BitDepth] += value.Length;
        stats.GroupsByOccupancy[reader.Count]++;
        PbtNodeGroupReader.Enumerator nodes = reader.EnumerateNodes();
        while (nodes.MoveNext())
        {
            stats.NodeCount++;
            stats.NodeEncodingBytes += nodes.Current.Length;
            if (nodes.Current[0] == 0) stats.LeafCount++;
            else stats.BranchCount++;
            stats.NodesByDepth[groupPath.BitDepth + PositionDepths[nodes.CurrentPosition]]++;
        }
    }

    private static int[] CreatePositionDepths()
    {
        int[] depths = new int[PbtFourLevelGroupGeometry.PositionCount];
        IPbtNodePath root = new PbtNodePath([], 0);
        for (int position = 0; position < depths.Length; position++)
            depths[position] = PbtFourLevelGroupGeometry.PathOf(root, position).BitDepth;
        return depths;
    }

    private static byte[][] CreateBounds(PbtColumns column, int rangeCount)
    {
        List<byte[]> bounds = [[]];
        if (column == PbtColumns.NodeGroups)
        {
            // Depth precedes the path in group keys; split paths within each depth as well.
            for (int depth = 0; depth <= PbtFourLevelGroupGeometry.MaxGroupDepth; depth += PbtFourLevelGroupGeometry.LevelsPerGroup)
            {
                int zoneBytes = depth >= 8 ? 1 : 0;
                int prefixBits = Math.Min(depth - zoneBytes * 8, 16);
                int partitions = Math.Min(rangeCount, 1 << prefixBits);
                ReadOnlySpan<byte> zones = zoneBytes == 0 ? [0] : [Eip8297KeyDerivation.AccountZone, Eip8297KeyDerivation.CodeZone, Eip8297KeyDerivation.StorageZone];
                foreach (byte zone in zones)
                    for (int partition = 0; partition < partitions; partition++)
                    {
                        byte[] boundary = new byte[4 + zoneBytes + (prefixBits + 7) / 8];
                        BinaryPrimitives.WriteUInt32BigEndian(boundary, (uint)depth);
                        if (zoneBytes != 0) boundary[4] = zone;
                        int prefix = (int)((long)partition * (1 << prefixBits) / partitions) << (16 - prefixBits);
                        int prefixOffset = 4 + zoneBytes;
                        if (boundary.Length > prefixOffset) boundary[prefixOffset] = (byte)(prefix >> 8);
                        if (boundary.Length > prefixOffset + 1) boundary[prefixOffset + 1] = (byte)prefix;
                        bounds.Add(boundary);
                    }
            }
        }
        else if (column == PbtColumns.Storages)
        {
            foreach (byte zone in new[] { Eip8297KeyDerivation.AccountZone, Eip8297KeyDerivation.StorageZone })
                for (int partition = 0; partition < rangeCount; partition++)
                {
                    byte[] boundary = new byte[3];
                    boundary[0] = zone;
                    BinaryPrimitives.WriteUInt16BigEndian(boundary.AsSpan(1), (ushort)((long)partition * PrefixSpace / rangeCount));
                    bounds.Add(boundary);
                }
        }
        else
        {
            for (int partition = 1; partition < rangeCount; partition++)
            {
                byte[] boundary = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(boundary, (ushort)((long)partition * PrefixSpace / rangeCount));
                bounds.Add(boundary);
            }
        }
        byte[] upper = new byte[5 + PbtStorageFullKey.MaxLength];
        Array.Fill(upper, byte.MaxValue);
        bounds.Add(upper);
        return bounds.ToArray();
    }
}

/// <summary>Inventory of persisted PBT records, without hash or reachability verification.</summary>
public sealed class PbtScanReport
{
    /// <summary>Stored whole-account records.</summary>
    public ColumnStats Accounts { get; } = new();
    /// <summary>Stored storage-word records.</summary>
    public ColumnStats Storages { get; } = new();
    /// <summary>Stored whole-bytecode records, including unreferenced code.</summary>
    public ColumnStats Codes { get; } = new();
    /// <summary>Stored node groups and their locally decoded shape.</summary>
    public NodeGroupStats NodeGroups { get; } = new();

    /// <summary>Gets the statistics for an active data column.</summary>
    public ColumnStats this[PbtColumns column] => column switch
    {
        PbtColumns.Accounts => Accounts,
        PbtColumns.Storages => Storages,
        PbtColumns.Codes => Codes,
        PbtColumns.NodeGroups => NodeGroups,
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    internal void MergeFrom(PbtScanReport other)
    {
        Accounts.MergeFrom(other.Accounts);
        Storages.MergeFrom(other.Storages);
        Codes.MergeFrom(other.Codes);
        NodeGroups.MergeFrom(other.NodeGroups);
        NodeGroups.NodeCount += other.NodeGroups.NodeCount;
        NodeGroups.LeafCount += other.NodeGroups.LeafCount;
        NodeGroups.BranchCount += other.NodeGroups.BranchCount;
        NodeGroups.NodeEncodingBytes += other.NodeGroups.NodeEncodingBytes;
        AddInto(NodeGroups.GroupsByDepth, other.NodeGroups.GroupsByDepth);
        AddInto(NodeGroups.PayloadBytesByDepth, other.NodeGroups.PayloadBytesByDepth);
        AddInto(NodeGroups.NodesByDepth, other.NodeGroups.NodesByDepth);
        AddInto(NodeGroups.GroupsByOccupancy, other.NodeGroups.GroupsByOccupancy);
    }

    private static void AddInto(long[] target, long[] source)
    {
        for (int index = 0; index < target.Length; index++) target[index] += source[index];
    }

    /// <summary>Formats stored sizes and node-group histograms.</summary>
    public string Format()
    {
        StringBuilder report = new();
        report.AppendLine();
        report.AppendLine("=== PBT scan: persisted inventory (not hash or reachability verification) ===");
        report.AppendLine($"  {"column",-12} {"records",15} {"key bytes",18} {"value bytes",18} {"total bytes",18} {"avg bytes",12}");
        foreach (PbtColumns column in new[] { PbtColumns.Accounts, PbtColumns.Storages, PbtColumns.Codes, PbtColumns.NodeGroups })
        {
            ColumnStats stats = this[column];
            report.AppendLine($"  {column,-12} {stats.RecordCount,15:N0} {stats.KeyBytes,18:N0} {stats.ValueBytes,18:N0} {stats.TotalBytes,18:N0} {stats.AverageRecordBytes,12:N1}");
        }
        report.AppendLine($"Contained nodes: {NodeGroups.NodeCount:N0} ({NodeGroups.LeafCount:N0} leaves, {NodeGroups.BranchCount:N0} branches), {NodeGroups.NodeEncodingBytes:N0} encoding bytes (excluding group keys and footers)");
        report.AppendLine("Node groups and contained nodes by bit depth");
        report.AppendLine($"  {"depth",6} {"groups",15} {"payload bytes",18} {"nodes",15}");
        for (int depth = 0; depth < NodeGroups.NodesByDepth.Length; depth++)
            if (NodeGroups.GroupsByDepth[depth] != 0 || NodeGroups.NodesByDepth[depth] != 0)
                report.AppendLine($"  {depth,6} {NodeGroups.GroupsByDepth[depth],15:N0} {NodeGroups.PayloadBytesByDepth[depth],18:N0} {NodeGroups.NodesByDepth[depth],15:N0}");
        report.AppendLine("Node-group occupancy");
        report.AppendLine($"  {"nodes",6} {"groups",15}");
        for (int occupancy = 0; occupancy < NodeGroups.GroupsByOccupancy.Length; occupancy++)
            if (NodeGroups.GroupsByOccupancy[occupancy] != 0)
                report.AppendLine($"  {occupancy,6} {NodeGroups.GroupsByOccupancy[occupancy],15:N0}");
        return report.ToString();
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
    }
}
