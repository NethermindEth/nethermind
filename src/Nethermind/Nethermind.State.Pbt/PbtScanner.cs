// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Serialization.Rlp;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>Validates typed flat columns and their canonical persisted EIP-8297 tree.</summary>
/// <remarks>Uses bounded-memory external sorting in the system temporary directory, with disk usage proportional to derived leaves.</remarks>
public sealed class PbtScanner(IColumnsDb<PbtColumns> db, IPbtConfig config, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<PbtScanner>();

    /// <summary>Scans logical entries and compressed-node records, returning detected violations.</summary>
    public Task<PbtScanReport> Scan(CancellationToken cancellationToken) => Scan(cancellationToken, null, 0, TimeProvider.System);

    internal Task<PbtScanReport> Scan(CancellationToken cancellationToken, string? temporaryParent, int bufferCapacity, TimeProvider timeProvider)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (config.ScanTreeConcurrency != 0 && _logger.IsInfo) _logger.Info("PBT scan runs serially; ScanTreeConcurrency is currently ignored.");
        PbtScanReport report = new();
        Progress progress = new(_logger, timeProvider);
        using PbtScanLeafSorter leaves = new(temporaryParent, bufferCapacity, progress: progress.Report, cleanupFailure: exception =>
        {
            if (_logger.IsWarn) _logger.Warn($"Unable to remove PBT scan temporary files: {exception}");
        });
        if (_logger.IsInfo) _logger.Info($"PBT scan uses bounded-memory sorting with temporary files in {leaves.TemporaryDirectory}; scratch disk usage is proportional to derived leaves.");
        ScanFlatEntries(report, leaves, progress, cancellationToken);
        report.PersistedRoot = PbtRocksDbPersistence.ReadCurrentState(db.GetColumnDb(PbtColumns.Metadata)).Root;
        long matchedNodes = 0;
        if (report.InvalidLeafCount == 0)
        {
            PbtScanTreeBuilder builder = new((path, encoding) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MatchesNode(path, encoding)) matchedNodes++;
                else report.InvalidNodeCount++;
            });
            progress.Report("sorting leaves", 0);
            foreach ((PbtStorageFullKey key, ValueHash256 value) in leaves.GetSorted(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (report.LeafCount == 0) progress.Report("folding tree", 0);
                builder.Add(key, value);
                report.LeafCount++;
                progress.Report("folding tree", report.LeafCount);
            }
            report.ComputedRoot = builder.Finish();
            report.RootMatches = report.ComputedRoot == report.PersistedRoot;
        }
        ScanNodes(report, progress, cancellationToken);
        // Each expected path is visited exactly once. Every actual node not successfully matched
        // is corrupt or unreachable, so no database-sized visited set is needed.
        report.InvalidNodeCount += report.NodeCount - matchedNodes;
        if (_logger.IsInfo) _logger.Info($"PBT scan completed: {report.LeafCount:N0} full leaves and {report.NodeCount:N0} compressed nodes.");
        return Task.FromResult(report);
    }

    private void ScanFlatEntries(PbtScanReport report, PbtScanLeafSorter leaves, Progress progress, CancellationToken cancellationToken)
    {
        foreach (PbtColumns columnName in new[] { PbtColumns.Accounts, PbtColumns.Storages, PbtColumns.Codes })
        {
            string phase = $"scanning {columnName}";
            long entries = 0;
            progress.Report(phase, 0);
            using ISortedView view = OpenView(columnName);
            while (view.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                report.LeafKeyBytes += view.CurrentKey.Length;
                report.LeafBytes += view.CurrentValue.Length;
                // Only data decoding is caught here: sort-file I/O failures must abort the scan.
                Account? account = null;
                CodeInfo? code = null;
                ValueHash256 hash = default;
                bool valid = true;
                try
                {
                    if (columnName == PbtColumns.Storages)
                    {
                        if (!IsStorageKey(view.CurrentKey) || view.CurrentValue.Length != ValueHash256.MemorySize
                            || view.CurrentValue.IndexOfAnyExcept((byte)0) < 0)
                            throw new InvalidDataException("Invalid persisted PBT storage entry.");
                    }
                    else
                    {
                        if (view.CurrentKey.Length != ValueHash256.MemorySize)
                            throw new InvalidDataException("Invalid persisted PBT hash key.");
                        hash = new(view.CurrentKey);
                        if (columnName == PbtColumns.Accounts)
                        {
                            RlpReader reader = new(view.CurrentValue);
                            account = AccountDecoder.Instance.Decode(ref reader)
                                ?? throw new InvalidDataException("Invalid persisted PBT account.");
                            if (reader.Position != reader.Length) throw new InvalidDataException("Trailing PBT account bytes.");
                            if (account.HasCode)
                            {
                                byte[] bytes = db.GetColumnDb(PbtColumns.Codes).Get(account.CodeHash.Bytes, ReadFlags.HintCacheMiss)
                                    ?? throw new InvalidDataException("Missing PBT bytecode.");
                                if (Keccak.Compute(bytes) != account.CodeHash) throw new InvalidDataException("Invalid PBT bytecode hash.");
                                code = new CodeInfo(bytes) { CodeHash = account.CodeHash.ValueHash256 };
                            }
                        }
                        else if (Keccak.Compute(view.CurrentValue).ValueHash256 != hash)
                            throw new InvalidDataException("PBT bytecode does not match its content hash.");
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or RlpException or ArgumentException or IndexOutOfRangeException or OverflowException)
                {
                    report.InvalidLeafCount++;
                    valid = false;
                }
                if (valid)
                {
                    if (columnName == PbtColumns.Storages)
                        leaves.Add(new PbtStorageFullKey(view.CurrentKey), new ValueHash256(view.CurrentValue), cancellationToken);
                    else if (account is not null)
                        foreach ((PbtFullKey key, ValueHash256 value) in PbtFlatState.AccountLeaves(hash, account, code))
                        {
                            leaves.Add((PbtStorageFullKey)key, value, cancellationToken);
                            progress.Report(phase, entries);
                        }
                }
                progress.Report(phase, ++entries);
            }
        }
    }

    private static bool IsStorageKey(ReadOnlySpan<byte> key) =>
        (key.Length == Eip8297KeyDerivation.StorageKeyLength && key[0] == Eip8297KeyDerivation.StorageZone)
        || (key.Length == Eip8297KeyDerivation.AccountKeyLength && key[0] == Eip8297KeyDerivation.AccountZone
            && key[^1] >= PbtKeyDerivation.HeaderStorageOffset && key[^1] < PbtKeyDerivation.CodeOffset);

    private bool MatchesNode(IPbtNodePath path, byte[] encoding)
    {
        PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
        byte[]? payload = db.GetColumnDb(PbtColumns.NodeGroups).Get(location.GroupKey.Encode(), ReadFlags.HintCacheMiss);
        if (payload is null) return false;
        try
        {
            PbtNodeGroupReader reader = new(location.GroupKey, payload);
            return reader.GetNode(location.Position).SequenceEqual(encoding);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException) { return false; }
    }

    private void ScanNodes(PbtScanReport report, Progress progress, CancellationToken cancellationToken)
    {
        progress.Report("scanning node groups", 0);
        using ISortedView view = OpenView(PbtColumns.NodeGroups);
        while (view.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                IPbtNodePath groupKey = PbtPathOperations.Decode(view.CurrentKey);
                PbtNodeGroupReader reader = new(groupKey, view.CurrentValue);
                PbtNodeGroupReader.Enumerator enumerator = reader.EnumerateNodes();
                while (enumerator.MoveNext())
                {
                    IPbtNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, enumerator.CurrentPosition);
                    report.NodeCount++;
                    report.NodeKeyBytes += path.Encode().Length;
                    report.NodeBytes += enumerator.Current.Length;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                report.InvalidNodeCount++;
            }
            progress.Report("scanning node groups", report.NodeCount);
        }
    }

    private ISortedView OpenView(PbtColumns columnName)
    {
        IDb column = db.GetColumnDb(columnName);
        if (column is not ISortedKeyValueStore sorted)
            throw new InvalidOperationException($"The PBT {columnName} column is a {column.GetType().Name}, which cannot be range scanned.");
        byte[] upper = new byte[PbtStorageFullKey.MaxLength + 1];
        Array.Fill(upper, byte.MaxValue);
        return sorted.GetViewBetween([], upper, ReadFlags.HintCacheMiss);
    }

    private sealed class Progress(ILogger logger, TimeProvider timeProvider)
    {
        private readonly long _started = timeProvider.GetTimestamp();
        private long _lastLog;
        private string? _phase;
        private long _lastCount;
        internal void Report(string phase, long count)
        {
            long now = timeProvider.GetTimestamp();
            double interval = timeProvider.GetElapsedTime(_lastLog, now).TotalSeconds;
            if ((_phase == phase || count != 0) && interval < 10) return;
            double rate = _phase == phase && interval > 0 ? Math.Max(0, count - _lastCount) / interval : 0;
            _phase = phase;
            _lastLog = now;
            _lastCount = count;
            double elapsed = timeProvider.GetElapsedTime(_started, now).TotalSeconds;
            if (logger.IsInfo) logger.Info($"PBT scan {phase}: {count:N0} items, elapsed {elapsed:N1}s, {rate:N0} items/s.");
        }
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
