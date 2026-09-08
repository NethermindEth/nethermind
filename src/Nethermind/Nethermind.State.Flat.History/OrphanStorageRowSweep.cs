// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

public sealed class OrphanStorageRowSweep(
    IColumnsDb<FlatHistoryColumns> history,
    IColumnsDb<FlatDbColumns> flat,
    HistoryAvailability availability,
    HistoryRowFormat rowFormat,
    SweepPacer pacer,
    ILogManager logManager)
    : PacedSweep(flat, "OrphanStorageRowsSwept", "OrphanStorageRowsSweepProgress", TallyLength, pacer, logManager.GetClassLogger<OrphanStorageRowSweep>())
{
    private const int RowsScanned = 0;
    private const int OrphanRows = 1;
    private const int OrphanAccounts = 2;
    private const int TallyLength = 3;
    private const int RowsPerBatch = 1 << 16;
    private const int BudgetCheckInterval = 1 << 10;
    private const int IdentityLength = BaseFlatPersistence.AccountKeyLength;
    private const int BlockBytes = sizeof(ulong);
    private const int StorageRowKeyLength = BaseFlatPersistence.StorageKeyLength + BlockBytes;
    private const int AccountRowKeyLength = Hash256.Size + BlockBytes;

    private readonly object _completionLock = new();
    private readonly HashSet<ValueHash256> _orphanIdentities = [];
    private Action<OrphanStorageRowReport>? _completed;
    private OrphanStorageRowReport? _completedReport;
    private long _orphanPrefix = -1;

    public event Action<OrphanStorageRowReport> Completed
    {
        add
        {
            OrphanStorageRowReport? latched;
            lock (_completionLock)
            {
                latched = _completedReport;
                if (latched is null) _completed += value;
            }

            if (latched is { } report) value(report);
        }
        remove
        {
            lock (_completionLock)
            {
                _completed -= value;
            }
        }
    }

    public bool Supported => !rowFormat.IsV3 && !availability.TryGetGlobalFloor(out _);

    public OrphanStorageRowReport Report => new(Tally[RowsScanned], Tally[OrphanRows], Tally[OrphanAccounts]);

    public bool TryGetCompletedReport(out OrphanStorageRowReport report)
    {
        lock (_completionLock)
        {
            report = _completedReport ?? default;
            return _completedReport is not null;
        }
    }

    internal new OrphanStorageRowReport RunToCompletion(bool repair, CancellationToken token)
    {
        base.RunToCompletion(repair, token);
        return Report;
    }

    protected override string Name(bool repair) => repair ? "Flat history orphan storage row sweep" : "Flat history orphan storage row check";

    protected override string StartDescription(bool repair) => repair
        ? "Flat history orphan storage row sweep starting in the background: every storage row is checked against the account row in force at its block, and the rows of accounts that were absent or had an empty storage root are deleted. It pauses for as long as it runs, resumes across restarts, and stamps the database when done."
        : "Flat history orphan storage row check starting in the background: every storage row is checked against the account row in force at its block; nothing is deleted.";

    protected override string Outcome()
    {
        OrphanStorageRowReport report = Report;
        return $"{report.RowsScanned:N0} rows scanned, {report.OrphanRows:N0} orphaned rows under {report.OrphanAccounts:N0} accounts";
    }

    protected override string ProgressDetail() => $"{Tally[RowsScanned]:N0} rows scanned, {Tally[OrphanAccounts]:N0} orphaned accounts so far";

    protected override bool FoundOrphans => Tally[OrphanAccounts] > 0;

    protected override bool NothingToJudge => base.NothingToJudge && (!availability.TryGetWatermark(out ulong watermark) || watermark == 0);

    protected override void OnCompleted()
    {
        OrphanStorageRowReport report = Report;
        Action<OrphanStorageRowReport>? subscribers;
        lock (_completionLock)
        {
            _completedReport = report;
            subscribers = _completed;
            _completed = null;
        }

        try
        {
            subscribers?.Invoke(report);
        }
        catch (Exception e)
        {
            if (Logger.IsError) Logger.Error("A consumer of the flat history orphan storage row sweep failed while handling its completion; the sweep itself is complete.", e);
        }
    }

    protected override bool Scan(bool repair, ReadOnlySpan<byte> start, long maxUnits, TimeSpan budget, out byte[]? next, CancellationToken token)
    {
        if (!Supported) throw new InvalidOperationException("The orphan storage row sweep reads post-value (v2) rows only.");

        ISortedKeyValueStore storageRows = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.StorageHistory);
        ISortedKeyValueStore accountRows = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.AccountHistory);
        byte[] startKey = start.ToArray();
        Dictionary<ValueHash256, AccountTimeline> timelines = [];
        List<byte[]> orphanRows = [];
        long passStartedAt = Stopwatch.GetTimestamp();
        long rowsThisPass = 0;
        long currentPrefix = -1;

        Span<byte> upper = stackalloc byte[StorageRowKeyLength + 1];
        upper.Fill(0xFF);
        using ISortedView view = storageRows.GetViewBetween(startKey, upper, ReadFlags.HintCacheMiss | ReadFlags.HintReadAhead);
        while (view.MoveNext())
        {
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != StorageRowKeyLength) continue;

            if (rowsThisPass >= maxUnits || ((rowsThisPass & (BudgetCheckInterval - 1)) == 0 && Stopwatch.GetElapsedTime(passStartedAt) >= budget))
            {
                token.ThrowIfCancellationRequested();
                Flush(repair, startKey, orphanRows);
                next = key.ToArray();
                return false;
            }

            long prefix = BinaryPrimitives.ReadUInt32BigEndian(key);
            if (prefix != currentPrefix)
            {
                token.ThrowIfCancellationRequested();
                currentPrefix = prefix;
                timelines.Clear();
                if (prefix != _orphanPrefix)
                {
                    _orphanIdentities.Clear();
                    _orphanPrefix = prefix;
                }

                LogProgress(repair, key);
            }

            rowsThisPass++;
            Tally[RowsScanned]++;
            if (view.CurrentValue.IsEmpty) continue;

            ValueHash256 identity = IdentityOf(key);
            if (!timelines.TryGetValue(identity, out AccountTimeline? timeline))
            {
                timeline = AccountTimeline.Load(accountRows, rowFormat, identity, token);
                timelines[identity] = timeline;
            }

            ulong block = rowFormat.DecodeSuffixBlock(key[BaseFlatPersistence.StorageKeyLength..]);
            if (timeline.HasStorageAt(block)) continue;

            Tally[OrphanRows]++;
            if (_orphanIdentities.Add(identity)) Tally[OrphanAccounts]++;
            if (!repair) continue;

            orphanRows.Add(key.ToArray());
            if (orphanRows.Count >= RowsPerBatch) Flush(repair, startKey, orphanRows);
        }

        Flush(repair, startKey, orphanRows);
        next = null;
        return true;
    }

    private void Flush(bool repair, byte[] startKey, List<byte[]> rows)
    {
        if (!repair || rows.Count == 0) return;

        WriteProgress(startKey);
        Flat.SyncWal();
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = history.StartWriteBatch())
        {
            IWriteBatch storage = batch.GetColumnBatch(FlatHistoryColumns.StorageHistory);
            foreach (byte[] row in rows) storage.Remove(row);
        }

        history.SyncWal();
        rows.Clear();
    }

    private static ValueHash256 IdentityOf(ReadOnlySpan<byte> storageRowKey)
    {
        Span<byte> identity = stackalloc byte[Hash256.Size];
        HistoryKeyLayout.Storage.ExtractAddressKey(storageRowKey[..BaseFlatPersistence.StorageKeyLength], identity[..IdentityLength]);
        return new ValueHash256(identity);
    }

    private sealed class AccountTimeline
    {
        private readonly List<(ulong Block, bool HasStorage)> _rows = [];

        public static AccountTimeline Load(ISortedKeyValueStore accountRows, HistoryRowFormat rowFormat, in ValueHash256 identity, CancellationToken token)
        {
            AccountTimeline timeline = new();
            Span<byte> lower = stackalloc byte[AccountRowKeyLength];
            Span<byte> upper = stackalloc byte[AccountRowKeyLength + 1];
            lower.Clear();
            upper.Fill(0xFF);
            identity.Bytes[..IdentityLength].CopyTo(lower);
            identity.Bytes[..IdentityLength].CopyTo(upper);
            upper[^1] = 0x00;
            using ISortedView view = accountRows.GetViewBetween(lower, upper, ReadFlags.HintCacheMiss);
            while (view.MoveNext())
            {
                token.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> key = view.CurrentKey;
                if (key.Length != AccountRowKeyLength) continue;

                ulong block = rowFormat.DecodeSuffixBlock(key[Hash256.Size..]);
                ReadOnlySpan<byte> value = view.CurrentValue;
                bool hasStorage = false;
                if (!value.IsEmpty)
                {
                    RlpReader reader = new(value);
                    hasStorage = AccountDecoder.Slim.DecodeStorageRootOnly(ref reader) != Keccak.EmptyTreeHash;
                }

                if (timeline._rows.Count > 0 && timeline._rows[^1].HasStorage == hasStorage) timeline._rows[^1] = (block, hasStorage);
                else timeline._rows.Add((block, hasStorage));
            }

            return timeline;
        }

        public bool HasStorageAt(ulong block)
        {
            int low = 0;
            int high = _rows.Count - 1;
            int newest = -1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                if (_rows[middle].Block <= block)
                {
                    newest = middle;
                    high = middle - 1;
                }
                else low = middle + 1;
            }

            return newest >= 0 && _rows[newest].HasStorage;
        }
    }
}

public readonly record struct OrphanStorageRowReport(long RowsScanned, long OrphanRows, long OrphanAccounts);
