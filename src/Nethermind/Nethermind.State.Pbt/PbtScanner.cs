// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Serialization.Rlp;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>Validates typed flat columns and their canonical persisted EIP-8297 tree.</summary>
/// <remarks>Tree leaves are derived only for comparing the logical state with node-group records.</remarks>
public sealed class PbtScanner(IColumnsDb<PbtColumns> db, IPbtConfig config, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtScanner>();

    /// <summary>Scans logical entries and compressed-node records, returning all detected violations.</summary>
    public Task<PbtScanReport> Scan(CancellationToken cancellationToken)
    {
        PbtScanReport report = new();
        List<KeyValuePair<PbtFullKey, ValueHash256>> leaves = [];

        ScanFlatEntries(report, leaves, cancellationToken);

        report.PersistedRoot = PbtRocksDbPersistence.ReadCurrentState(db.GetColumnDb(PbtColumns.Metadata)).Root;
        if (report.InvalidLeafCount == 0)
        {
            using PbtWriteBatchBuilder changes = new(0);
            foreach ((PbtFullKey key, ValueHash256 value) in leaves) changes.Set(key, value);
            using PbtNodeGroupStore nodeStore = new();
            report.ComputedRoot = TrieUpdater.UpdateRoot(nodeStore, default, changes.Build());
            report.RootMatches = report.ComputedRoot == report.PersistedRoot;
            List<PbtNodeRecord> expectedNodes = [];
            foreach (PbtNodePath groupKey in nodeStore.EnumerateNodeGroupKeys())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using RefCountingMemory? payload = nodeStore.GetNodeGroup(groupKey);
                PbtNodeGroupReader reader = new(groupKey, payload!.GetSpan());
                PbtNodeGroupReader.Enumerator enumerator = reader.EnumerateNodes();
                while (enumerator.MoveNext())
                    expectedNodes.Add(new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(groupKey, enumerator.CurrentPosition), enumerator.Current));
            }
            expectedNodes.Sort(static (left, right) => left.Path.CompareTo(right.Path));
            ScanNodes(report, expectedNodes, cancellationToken);
        }
        else
        {
            ScanNodes(report, [], cancellationToken);
        }

        if (_logger.IsInfo) _logger.Info($"PBT scan completed with {config.ScanTreeConcurrency} configured scan workers: {report.LeafCount:N0} full leaves and {report.NodeCount:N0} compressed nodes.");
        return Task.FromResult(report);
    }

    private void ScanFlatEntries(PbtScanReport report, List<KeyValuePair<PbtFullKey, ValueHash256>> leaves, CancellationToken cancellationToken)
    {
        Dictionary<ValueHash256, Account> accounts = [];
        Dictionary<ValueHash256, CodeInfo> codes = [];
        Dictionary<PbtFullKey, EvmWord> storages = [];
        foreach (PbtColumns columnName in new[] { PbtColumns.Accounts, PbtColumns.Storages, PbtColumns.Codes })
        {
            IDb column = db.GetColumnDb(columnName);
            if (column is not ISortedKeyValueStore sorted)
                throw new InvalidOperationException($"The PBT {columnName} column is a {column.GetType().Name}, which cannot be range scanned.");

            byte[] upper = new byte[PbtFullKey.MaxLength + 1];
            Array.Fill(upper, byte.MaxValue);
            using ISortedView view = sorted.GetViewBetween([], upper);
            while (view.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                report.LeafKeyBytes += view.CurrentKey.Length;
                report.LeafBytes += view.CurrentValue.Length;
                try
                {
                    if (columnName == PbtColumns.Storages)
                    {
                        if (!IsStorageKey(view.CurrentKey) || view.CurrentValue.Length != ValueHash256.MemorySize
                            || view.CurrentValue.IndexOfAnyExcept((byte)0) < 0)
                            throw new InvalidDataException("Invalid persisted PBT storage entry.");
                        storages.Add(new PbtFullKey(view.CurrentKey), EvmWordSlot.FromStripped(view.CurrentValue));
                    }
                    else
                    {
                        if (view.CurrentKey.Length != ValueHash256.MemorySize)
                            throw new InvalidDataException("Invalid persisted PBT hash key.");
                        ValueHash256 hash = new(view.CurrentKey);
                        if (columnName == PbtColumns.Accounts)
                        {
                            RlpReader reader = new(view.CurrentValue);
                            Account account = AccountDecoder.Instance.Decode(ref reader)
                                ?? throw new InvalidDataException("Invalid persisted PBT account.");
                            if (reader.Position != reader.Length) throw new InvalidDataException("Trailing PBT account bytes.");
                            accounts.Add(hash, account);
                        }
                        else
                        {
                            if (Keccak.Compute(view.CurrentValue).ValueHash256 != hash)
                                throw new InvalidDataException("PBT bytecode does not match its content hash.");
                            codes.Add(hash, new CodeInfo(view.CurrentValue.ToArray()) { CodeHash = hash });
                        }
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or RlpException or ArgumentException or IndexOutOfRangeException or OverflowException)
                {
                    report.InvalidLeafCount++;
                }
            }
        }

        foreach (Account account in accounts.Values)
            if (account.HasCode && !codes.ContainsKey(account.CodeHash.ValueHash256)) report.InvalidLeafCount++;
        if (report.InvalidLeafCount != 0) return;

        foreach (KeyValuePair<PbtFullKey, ValueHash256> leaf in PbtFlatState.EnumerateLeaves(accounts, storages, hash => codes.GetValueOrDefault(hash)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            leaves.Add(leaf);
        }
        report.LeafCount = leaves.Count;
    }

    private static bool IsStorageKey(ReadOnlySpan<byte> key) =>
        (key.Length == Eip8297KeyDerivation.StorageKeyLength && key[0] == Eip8297KeyDerivation.StorageZone)
        || (key.Length == Eip8297KeyDerivation.AccountKeyLength && key[0] == Eip8297KeyDerivation.AccountZone
            && key[^1] >= PbtKeyDerivation.HeaderStorageOffset && key[^1] < PbtKeyDerivation.CodeOffset);

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
                    PbtNodeGroupReader reader = new(groupKey, view.CurrentValue);
                    PbtNodeGroupReader.Enumerator enumerator = reader.EnumerateNodes();
                    while (enumerator.MoveNext())
                    {
                        PbtNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, enumerator.CurrentPosition);
                        actualNodes.Add(new PbtNodeRecord(path, enumerator.Current));
                        report.NodeCount++;
                        report.NodeKeyBytes += path.Encode().Length;
                        report.NodeBytes += enumerator.Current.Length;
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


}

/// <summary>Summary of a typed flat-state and PBT node-group scan.</summary>
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
        report.AppendLine($"Derived leaves: {LeafCount:N0} ({LeafBytes:N0} flat value bytes, {LeafKeyBytes:N0} flat key bytes)");
        report.AppendLine($"Compressed nodes: {NodeCount:N0} ({NodeBytes:N0} value bytes, {NodeKeyBytes:N0} key bytes)");
        report.AppendLine($"Computed root: {ComputedRoot}");
        report.AppendLine($"Persisted root: {PersistedRoot} ({(RootMatches ? "matches" : "MISMATCH")})");
        report.AppendLine($"Violations: {InvalidLeafCount + InvalidNodeCount:N0} ({InvalidLeafCount:N0} flat entries, {InvalidNodeCount:N0} nodes)");
        return report.ToString();
    }
}
