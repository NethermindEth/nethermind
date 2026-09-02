// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>Validates the full-key columns of a persisted EIP-8297 PBT database.</summary>
/// <remarks>
/// This diagnostic intentionally scans the canonical columns rather than interpreting obsolete
/// stem blobs or node groups. It validates state-layer leaf invariants and recomputes the
/// root from the visible full-key index.
/// </remarks>
public sealed class PbtScanner(IColumnsDb<PbtColumns> db, IPbtConfig config, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtScanner>();

    /// <summary>Scans full leaves and compressed-node records, returning all detected violations.</summary>
    public Task<PbtScanReport> Scan(CancellationToken cancellationToken)
    {
        PbtScanReport report = new();
        List<KeyValuePair<PbtFullKey, ValueHash256>> leaves = [];

        ScanLeaves(report, leaves, cancellationToken);

        report.PersistedRoot = PbtRocksDbPersistence.ReadCurrentState(db.GetColumnDb(PbtColumns.Metadata)).Root;
        if (report.InvalidLeafCount == 0)
        {
            PbtWriteBatch changes = new();
            foreach ((PbtFullKey key, ValueHash256 value) in leaves) changes.Set(key, value);
            PbtNodeGroupStore nodeStore = new();
            report.ComputedRoot = TrieUpdater.UpdateRoot(nodeStore, default, changes);
            report.RootMatches = report.ComputedRoot == report.PersistedRoot;
            ScanNodes(report, nodeStore.EnumerateRecords(), cancellationToken);
        }
        else
        {
            ScanNodes(report, [], cancellationToken);
        }

        if (_logger.IsInfo) _logger.Info($"PBT scan completed with {config.ScanTreeConcurrency} configured scan workers: {report.LeafCount:N0} full leaves and {report.NodeCount:N0} compressed nodes.");
        return Task.FromResult(report);
    }

    private void ScanLeaves(PbtScanReport report, List<KeyValuePair<PbtFullKey, ValueHash256>> leaves, CancellationToken cancellationToken)
    {
        IDb column = db.GetColumnDb(PbtColumns.FullLeaves);
        if (column is not ISortedKeyValueStore sorted)
        {
            throw new InvalidOperationException($"The PBT {PbtColumns.FullLeaves} column is a {column.GetType().Name}, which cannot be range scanned.");
        }

        using ISortedView view = sorted.GetViewBetween([], [0xFF, 0xFF]);
        while (view.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            report.LeafCount++;
            report.LeafKeyBytes += view.CurrentKey.Length;
            report.LeafBytes += view.CurrentValue.Length;

            if (!TryValidateLeaf(view.CurrentKey, view.CurrentValue, out PbtFullKey? key))
            {
                report.InvalidLeafCount++;
                continue;
            }

            leaves.Add(new KeyValuePair<PbtFullKey, ValueHash256>(key!, new ValueHash256(view.CurrentValue)));
        }
    }

    private void ScanNodes(PbtScanReport report, IReadOnlyList<PbtNodeRecord> expectedNodes, CancellationToken cancellationToken)
    {
        IDb column = db.GetColumnDb(PbtColumns.NodeGroups);
        if (column is not ISortedKeyValueStore sorted)
        {
            throw new InvalidOperationException($"The PBT {PbtColumns.NodeGroups} column is a {column.GetType().Name}, which cannot be range scanned.");
        }

        List<PbtNodeRecord> actualNodes = [];
        int malformedGroupCount = 0;
        using (ISortedView view = sorted.GetViewBetween([], [0xFF, 0xFF]))
        {
            while (view.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    PbtNodePath groupKey = DecodeGroupKey(view.CurrentKey);
                    PbtNodeGroup group = PbtNodeGroupCodec.Decode(groupKey, view.CurrentValue);
                    foreach (PbtNodeRecord node in group.EnumerateNodes())
                    {
                        actualNodes.Add(node);
                        report.NodeCount++;
                        report.NodeKeyBytes += node.Path.Encode().Length;
                        report.NodeBytes += node.Encoding.Length;
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
                {
                    malformedGroupCount++;
                }
            }
        }

        actualNodes.Sort(static (left, right) => left.Path.CompareTo(right.Path));
        int commonCount = Math.Min(actualNodes.Count, expectedNodes.Count);
        for (int index = 0; index < commonCount; index++)
        {
            if (!actualNodes[index].Path.Equals(expectedNodes[index].Path)
                || !actualNodes[index].Encoding.Span.SequenceEqual(expectedNodes[index].Encoding.Span))
            {
                report.InvalidNodeCount++;
            }
        }
        report.InvalidNodeCount += Math.Max(malformedGroupCount, Math.Abs(actualNodes.Count - expectedNodes.Count));
    }

    private static PbtNodePath DecodeGroupKey(ReadOnlySpan<byte> encoding)
    {
        PbtNodePath groupKey = PbtNodePath.Decode(encoding);
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new InvalidDataException("A persisted PBT node-group key depth must be a four-level boundary.");
        return groupKey;
    }

    private static bool TryValidateLeaf(ReadOnlySpan<byte> keyBytes, ReadOnlySpan<byte> value, out PbtFullKey? key)
    {
        key = null;
        if (value.Length != ValueHash256.MemorySize || value.IndexOfAnyExcept((byte)0) < 0) return false;
        if (keyBytes.Length is not (Eip8297KeyDerivation.AccountKeyLength or Eip8297KeyDerivation.StorageKeyLength)) return false;

        byte zone = keyBytes[0];
        if (zone is Eip8297KeyDerivation.AccountZone or Eip8297KeyDerivation.CodeZone)
        {
            if (keyBytes.Length != Eip8297KeyDerivation.AccountKeyLength) return false;
        }
        else if (zone == Eip8297KeyDerivation.StorageZone)
        {
            if (keyBytes.Length != Eip8297KeyDerivation.StorageKeyLength) return false;
        }
        else
        {
            return false;
        }

        key = new PbtFullKey(keyBytes);
        return true;
    }
}

/// <summary>Summary of a full-key PBT database scan.</summary>
public sealed class PbtScanReport
{
    public long LeafCount { get; internal set; }
    public long LeafKeyBytes { get; internal set; }
    public long LeafBytes { get; internal set; }
    public long NodeCount { get; internal set; }
    public long NodeKeyBytes { get; internal set; }
    public long NodeBytes { get; internal set; }
    public long InvalidLeafCount { get; internal set; }
    public long InvalidNodeCount { get; internal set; }
    public ValueHash256 ComputedRoot { get; internal set; }
    public ValueHash256 PersistedRoot { get; internal set; }
    public bool RootMatches { get; internal set; }
    public bool IsValid => InvalidLeafCount == 0 && InvalidNodeCount == 0 && RootMatches;

    /// <summary>Formats the diagnostic report for the startup step.</summary>
    public string Format()
    {
        StringBuilder report = new();
        report.AppendLine();
        report.AppendLine("=== PBT scan ===");
        report.AppendLine($"Full leaves: {LeafCount:N0} ({LeafBytes:N0} value bytes, {LeafKeyBytes:N0} key bytes)");
        report.AppendLine($"Compressed nodes: {NodeCount:N0} ({NodeBytes:N0} value bytes, {NodeKeyBytes:N0} key bytes)");
        report.AppendLine($"Computed root: {ComputedRoot}");
        report.AppendLine($"Persisted root: {PersistedRoot} ({(RootMatches ? "matches" : "MISMATCH")})");
        report.AppendLine($"Violations: {InvalidLeafCount + InvalidNodeCount:N0} ({InvalidLeafCount:N0} leaves, {InvalidNodeCount:N0} nodes)");
        return report.ToString();
    }
}
