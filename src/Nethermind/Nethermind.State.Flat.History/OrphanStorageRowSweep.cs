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
    IPersistenceManager persistenceManager,
    HistoryRowFormat rowFormat,
    ILogManager logManager) : IDisposable
{
    private static readonly byte[] MarkerKey = Keccak.Compute("OrphanStorageRowsSwept").BytesToArray();
    private static readonly byte[] CursorKey = Keccak.Compute("OrphanStorageRowsSweepCursor").BytesToArray();
    private static readonly byte[] TallyKey = Keccak.Compute("OrphanStorageRowsSweepTally").BytesToArray();
    private const byte Swept = 1;
    private const byte FormatUnsupported = 2;
    private const int RowsPerBatch = 1 << 16;
    private const int IdentityLength = BaseFlatPersistence.AccountKeyLength;
    private const int BlockBytes = sizeof(ulong);
    private const int StorageRowKeyLength = BaseFlatPersistence.StorageKeyLength + BlockBytes;
    private const int AccountRowKeyLength = Hash256.Size + BlockBytes;
    private static readonly TimeSpan PassBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DrainPoll = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger = logManager.GetClassLogger<OrphanStorageRowSweep>();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _loop;
    private long _checkCursor;
    private bool _tallyLoaded;
    private long _lastProgressAt;
    private long _rowsScanned;
    private long _orphanRows;
    private long _orphanAccounts;

    public event Action<OrphanStorageRowReport>? Completed;

    public bool Supported => !rowFormat.IsV3;

    public bool AlreadyHandled => flat.GetColumnDb(FlatDbColumns.Metadata).Get(MarkerKey) is [Swept or FormatUnsupported];

    public OrphanStorageRowReport Report => new(_rowsScanned, _orphanRows, _orphanAccounts);

    public void MarkFormatUnsupported() => flat.GetColumnDb(FlatDbColumns.Metadata).PutSpan(MarkerKey, [FormatUnsupported]);

    public void Start(bool repair, ulong drainedAtBlock)
    {
        if (_loop is not null) return;

        _loop = new Thread(() => RunLoop(repair, drainedAtBlock)) { IsBackground = true, Name = "Flat history orphan storage row sweep" };
        _loop.Start();
    }

    internal OrphanStorageRowReport RunToCompletion(bool repair, CancellationToken token)
    {
        while (!RunOnePass(repair, long.MaxValue, TimeSpan.MaxValue, token))
        {
        }

        return Report;
    }

    internal bool RunOnePass(bool repair, long maxRows, TimeSpan budget, CancellationToken token)
    {
        if (!Supported) throw new InvalidOperationException("The orphan storage row sweep reads post-value (v2) rows only.");

        ISortedKeyValueStore storageRows = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.StorageHistory);
        ISortedKeyValueStore accountRows = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.AccountHistory);
        long startPrefix = repair ? ReadCursor() : _checkCursor;
        if (startPrefix > uint.MaxValue) return true;
        if (startPrefix == 0) ResetTally();
        else if (repair && !_tallyLoaded) LoadTally();
        _tallyLoaded = true;

        Dictionary<ValueHash256, AccountTimeline> timelines = [];
        HashSet<ValueHash256> orphanIdentities = [];
        List<byte[]> orphanRows = [];
        long passStartedAt = Stopwatch.GetTimestamp();
        long rowsThisPass = 0;
        long currentPrefix = -1;
        long nextPrefix = uint.MaxValue + 1L;
        bool completed = true;

        Span<byte> lower = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(lower, (uint)startPrefix);
        Span<byte> upper = stackalloc byte[StorageRowKeyLength + 1];
        upper.Fill(0xFF);
        using (ISortedView view = storageRows.GetViewBetween(startPrefix == 0 ? ReadOnlySpan<byte>.Empty : lower, upper, ReadFlags.HintCacheMiss | ReadFlags.HintReadAhead))
        {
            while (view.MoveNext())
            {
                token.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> key = view.CurrentKey;
                if (key.Length != StorageRowKeyLength) continue;

                long prefix = BinaryPrimitives.ReadUInt32BigEndian(key);
                if (prefix != currentPrefix)
                {
                    if (currentPrefix >= 0 && (rowsThisPass >= maxRows || Stopwatch.GetElapsedTime(passStartedAt) >= budget))
                    {
                        nextPrefix = prefix;
                        completed = false;
                        break;
                    }

                    currentPrefix = prefix;
                    timelines.Clear();
                    LogProgress(repair, prefix);
                }

                rowsThisPass++;
                _rowsScanned++;
                if (view.CurrentValue.IsEmpty) continue;

                ValueHash256 identity = IdentityOf(key);
                if (!timelines.TryGetValue(identity, out AccountTimeline? timeline))
                {
                    timeline = AccountTimeline.Load(accountRows, rowFormat, identity, token);
                    timelines[identity] = timeline;
                }

                ulong block = rowFormat.DecodeSuffixBlock(key[BaseFlatPersistence.StorageKeyLength..]);
                if (timeline.HasStorageAt(block)) continue;

                _orphanRows++;
                if (orphanIdentities.Add(identity)) _orphanAccounts++;
                if (!repair) continue;

                orphanRows.Add(key.ToArray());
                if (orphanRows.Count >= RowsPerBatch) Delete(orphanRows);
            }
        }

        if (repair)
        {
            Delete(orphanRows);
            IDb metadata = flat.GetColumnDb(FlatDbColumns.Metadata);
            if (completed)
            {
                Announce(Report);
                metadata.Remove(CursorKey);
                metadata.Remove(TallyKey);
                metadata.PutSpan(MarkerKey, [Swept]);
            }
            else
            {
                WriteCursor((uint)nextPrefix);
                WriteTally();
            }
        }
        else _checkCursor = nextPrefix;

        return completed;
    }

    internal bool TryStampFresh()
    {
        StateId persisted = persistenceManager.GetCurrentPersistedStateId();
        if (persisted != StateId.PreGenesis && persisted != StateId.Sync) return false;

        flat.GetColumnDb(FlatDbColumns.Metadata).PutSpan(MarkerKey, [Swept]);
        return true;
    }

    private bool Drained(ulong drainedAtBlock)
    {
        StateId persisted = persistenceManager.GetCurrentPersistedStateId();
        return persisted != StateId.PreGenesis && persisted != StateId.Sync && persisted.BlockNumber >= drainedAtBlock;
    }

    private void Announce(OrphanStorageRowReport report)
    {
        try
        {
            Completed?.Invoke(report);
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("A consumer of the flat history orphan storage row sweep failed while handling its completion; the sweep itself is complete.", e);
        }
    }

    private void RunLoop(bool repair, ulong drainedAtBlock)
    {
        CancellationToken token = _cts.Token;
        try
        {
            if (repair && TryStampFresh())
            {
                if (_logger.IsInfo) _logger.Info("Flat history orphan storage row sweep skipped: this database has no persisted block state, so its history holds nothing to judge; recorded as swept.");
                return;
            }

            if (!Drained(drainedAtBlock))
            {
                if (_logger.IsInfo) _logger.Info($"Flat history orphan storage row {(repair ? "sweep" : "check")} waits for the persisted flat state to reach block {drainedAtBlock}, so that everything the previous binary left queued has landed first.");
                while (!Drained(drainedAtBlock))
                {
                    if (token.WaitHandle.WaitOne(DrainPoll)) return;
                }
            }

            if (_logger.IsInfo) _logger.Info(repair
                ? "Flat history orphan storage row sweep starting in the background: every storage row is checked against the account row in force at its block, and the rows of accounts that were absent or had an empty storage root are deleted. It pauses for as long as it runs, resumes across restarts, and stamps the database when done."
                : "Flat history orphan storage row check starting in the background: every storage row is checked against the account row in force at its block; nothing is deleted.");
            long startedAt = Stopwatch.GetTimestamp();
            while (!token.IsCancellationRequested)
            {
                long passStartedAt = Stopwatch.GetTimestamp();
                if (RunOnePass(repair, long.MaxValue, PassBudget, token)) break;

                if (token.WaitHandle.WaitOne(Stopwatch.GetElapsedTime(passStartedAt))) return;
            }

            if (token.IsCancellationRequested) return;

            OrphanStorageRowReport report = Report;
            string outcome = $"{report.RowsScanned:N0} rows scanned, {report.OrphanRows:N0} orphaned rows under {report.OrphanAccounts:N0} accounts; this start took {Stopwatch.GetElapsedTime(startedAt)}.";
            if (repair)
            {
                if (_logger.IsInfo) _logger.Info($"Flat history orphan storage row sweep done, orphaned rows deleted: {outcome}");
            }
            else if (report.OrphanAccounts > 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Flat history orphan storage row check FAILED: {outcome} Set FlatDb.SweepOrphanStorage to delete them.");
            }
            else if (_logger.IsInfo) _logger.Info($"Flat history orphan storage row check passed: {outcome}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("The flat history orphan storage row sweep failed; it resumes from its cursor on the next start.", e);
        }
    }

    private void Delete(List<byte[]> rows)
    {
        if (rows.Count == 0) return;

        using (IColumnsWriteBatch<FlatHistoryColumns> batch = history.StartWriteBatch())
        {
            IWriteBatch storage = batch.GetColumnBatch(FlatHistoryColumns.StorageHistory);
            foreach (byte[] row in rows) storage.Remove(row);
        }

        rows.Clear();
    }

    private void LogProgress(bool repair, long prefix)
    {
        if (!_logger.IsInfo) return;
        if (_lastProgressAt != 0 && Stopwatch.GetElapsedTime(_lastProgressAt) < ProgressInterval) return;

        _lastProgressAt = Stopwatch.GetTimestamp();
        _logger.Info($"Flat history orphan storage row {(repair ? "sweep" : "check")} at {prefix / (double)(1L << 32):P1} of the key space: {_rowsScanned:N0} rows scanned, {_orphanAccounts:N0} orphaned accounts so far.");
    }

    private void ResetTally()
    {
        _rowsScanned = 0;
        _orphanRows = 0;
        _orphanAccounts = 0;
    }

    private void LoadTally()
    {
        byte[]? value = flat.GetColumnDb(FlatDbColumns.Metadata).Get(TallyKey);
        if (value is not { Length: 3 * sizeof(long) }) return;

        _rowsScanned = BinaryPrimitives.ReadInt64BigEndian(value);
        _orphanRows = BinaryPrimitives.ReadInt64BigEndian(value.AsSpan(8));
        _orphanAccounts = BinaryPrimitives.ReadInt64BigEndian(value.AsSpan(16));
    }

    private void WriteTally()
    {
        Span<byte> value = stackalloc byte[3 * sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(value, _rowsScanned);
        BinaryPrimitives.WriteInt64BigEndian(value[8..], _orphanRows);
        BinaryPrimitives.WriteInt64BigEndian(value[16..], _orphanAccounts);
        flat.GetColumnDb(FlatDbColumns.Metadata).PutSpan(TallyKey, value);
    }

    private long ReadCursor()
    {
        byte[]? value = flat.GetColumnDb(FlatDbColumns.Metadata).Get(CursorKey);
        return value is { Length: sizeof(uint) } ? BinaryPrimitives.ReadUInt32BigEndian(value) : 0;
    }

    private void WriteCursor(uint prefix)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(value, prefix);
        flat.GetColumnDb(FlatDbColumns.Metadata).PutSpan(CursorKey, value);
    }

    private static ValueHash256 IdentityOf(ReadOnlySpan<byte> storageRowKey)
    {
        Span<byte> identity = stackalloc byte[Hash256.Size];
        HistoryKeyLayout.Storage.ExtractAddressKey(storageRowKey[..BaseFlatPersistence.StorageKeyLength], identity[..IdentityLength]);
        return new ValueHash256(identity);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _loop?.Join();
        _cts.Dispose();
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
