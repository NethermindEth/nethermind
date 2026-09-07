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

namespace Nethermind.State.Flat;

public sealed class OrphanStorageSweep(IColumnsDb<FlatDbColumns> db, IPersistenceManager persistenceManager, ILogManager logManager) : IDisposable
{
    private static readonly byte[] MarkerKey = Keccak.Compute("OrphanStorageSwept").BytesToArray();
    private static readonly byte[] CursorKey = Keccak.Compute("OrphanStorageSweepCursor").BytesToArray();
    private const byte Swept = 1;
    private const byte LayoutUnsupported = 2;
    private const int IdentitiesPerBatch = 256;
    private const int AccountBuffer = 256;
    private const int IdentityLength = BaseFlatPersistence.AccountKeyLength;
    private const int StorageKeyLength = BaseFlatPersistence.StorageKeyLength;
    private const int SlotOffset = BasePersistence.StoragePrefixPortion;
    private const int SuffixOffset = SlotOffset + Hash256.Size;
    private static readonly TimeSpan PassBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DrainPoll = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger = logManager.GetClassLogger<OrphanStorageSweep>();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _loop;
    private long _checkCursor;
    private long _lastProgressAt;
    private long _slotsScanned;
    private long _orphanSlots;
    private long _orphanAccounts;
    private long _missingAccounts;
    private long _emptyRootAccounts;

    public bool AlreadyHandled => db.GetColumnDb(FlatDbColumns.Metadata).Get(MarkerKey) is [Swept or LayoutUnsupported];

    public OrphanStorageReport Report => new(_slotsScanned, _orphanSlots, _orphanAccounts, _missingAccounts, _emptyRootAccounts);

    public void MarkLayoutUnsupported() => db.GetColumnDb(FlatDbColumns.Metadata).PutSpan(MarkerKey, [LayoutUnsupported]);

    public void Start(bool repair, ulong drainedAtBlock)
    {
        if (_loop is not null) return;

        _loop = new Thread(() => RunLoop(repair, drainedAtBlock)) { IsBackground = true, Name = "Flat orphan storage sweep" };
        _loop.Start();
    }

    internal OrphanStorageReport RunToCompletion(bool repair, CancellationToken token)
    {
        while (!RunOnePass(repair, long.MaxValue, TimeSpan.MaxValue, token))
        {
        }

        return Report;
    }

    internal bool RunOnePass(bool repair, long maxSlots, TimeSpan budget, CancellationToken token)
    {
        ISortedKeyValueStore storage = (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage);
        IReadOnlyKeyValueStore accounts = db.GetColumnDb(FlatDbColumns.Account);
        long startPrefix = repair ? ReadCursor() : _checkCursor;
        if (startPrefix > uint.MaxValue) return true;

        Dictionary<ValueHash256, bool> decided = [];
        List<ValueHash256> orphans = [];
        long passStartedAt = Stopwatch.GetTimestamp();
        long slotsThisPass = 0;
        long currentPrefix = -1;
        long nextPrefix = uint.MaxValue + 1L;
        bool completed = true;

        Span<byte> lower = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(lower, (uint)startPrefix);
        Span<byte> upper = stackalloc byte[StorageKeyLength + 1];
        upper.Fill(0xFF);
        using (ISortedView view = storage.GetViewBetween(startPrefix == 0 ? ReadOnlySpan<byte>.Empty : lower, upper, ReadFlags.HintCacheMiss | ReadFlags.HintReadAhead))
        {
            while (view.MoveNext())
            {
                token.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> key = view.CurrentKey;
                if (key.Length != StorageKeyLength) continue;

                long prefix = BinaryPrimitives.ReadUInt32BigEndian(key);
                if (prefix != currentPrefix)
                {
                    if (currentPrefix >= 0 && (slotsThisPass >= maxSlots || Stopwatch.GetElapsedTime(passStartedAt) >= budget))
                    {
                        nextPrefix = prefix;
                        completed = false;
                        break;
                    }

                    currentPrefix = prefix;
                    decided.Clear();
                    LogProgress(prefix);
                }

                slotsThisPass++;
                _slotsScanned++;
                ValueHash256 identity = IdentityOf(key);
                if (!decided.TryGetValue(identity, out bool orphan))
                {
                    orphan = IsOrphan(accounts, identity, out bool missing);
                    decided[identity] = orphan;
                    if (orphan)
                    {
                        _orphanAccounts++;
                        if (missing) _missingAccounts++;
                        else _emptyRootAccounts++;
                        if (repair) orphans.Add(identity);
                    }
                }

                if (orphan) _orphanSlots++;
            }
        }

        if (repair)
        {
            Delete(accounts, orphans, token);
            if (completed)
            {
                db.GetColumnDb(FlatDbColumns.Metadata).Remove(CursorKey);
                db.GetColumnDb(FlatDbColumns.Metadata).PutSpan(MarkerKey, [Swept]);
            }
            else WriteCursor((uint)nextPrefix);
        }
        else _checkCursor = nextPrefix;

        return completed;
    }

    internal bool TryStampFresh()
    {
        StateId persisted = persistenceManager.GetCurrentPersistedStateId();
        if (persisted != StateId.PreGenesis && persisted != StateId.Sync) return false;

        db.GetColumnDb(FlatDbColumns.Metadata).PutSpan(MarkerKey, [Swept]);
        return true;
    }

    private bool Drained(ulong drainedAtBlock)
    {
        StateId persisted = persistenceManager.GetCurrentPersistedStateId();
        return persisted != StateId.PreGenesis && persisted != StateId.Sync && persisted.BlockNumber >= drainedAtBlock;
    }

    private void RunLoop(bool repair, ulong drainedAtBlock)
    {
        CancellationToken token = _cts.Token;
        try
        {
            if (repair && TryStampFresh())
            {
                if (_logger.IsInfo) _logger.Info("Flat orphan storage sweep skipped: this database has no persisted state yet, so every slot it will hold is written by a version that never leaves orphans; recorded as swept.");
                return;
            }

            if (!Drained(drainedAtBlock))
            {
                if (_logger.IsInfo) _logger.Info($"Flat orphan storage {(repair ? "sweep" : "check")} waits for the persisted flat state to reach block {drainedAtBlock}, so that everything the previous binary left queued has landed first.");
                while (!Drained(drainedAtBlock))
                {
                    if (token.WaitHandle.WaitOne(DrainPoll)) return;
                }
            }

            if (_logger.IsInfo) _logger.Info(repair
                ? "Flat orphan storage sweep starting in the background: every storage slot is checked against its account, and the slots of accounts that are absent or have an empty storage root are deleted. It pauses for as long as it runs, resumes across restarts, and stamps the database when done."
                : "Flat orphan storage check starting in the background: every storage slot is checked against its account; nothing is deleted.");
            long startedAt = Stopwatch.GetTimestamp();
            while (!token.IsCancellationRequested)
            {
                long passStartedAt = Stopwatch.GetTimestamp();
                if (RunOnePass(repair, long.MaxValue, PassBudget, token)) break;

                if (token.WaitHandle.WaitOne(Stopwatch.GetElapsedTime(passStartedAt))) return;
            }

            if (token.IsCancellationRequested) return;

            OrphanStorageReport report = Report;
            string outcome = $"{report.SlotsScanned:N0} slots scanned, {report.OrphanSlots:N0} orphaned slots under {report.OrphanAccounts:N0} accounts ({report.MissingAccounts:N0} absent, {report.EmptyRootAccounts:N0} with an empty storage root) in {Stopwatch.GetElapsedTime(startedAt)} since this start.";
            if (repair)
            {
                if (_logger.IsInfo) _logger.Info($"Flat orphan storage sweep done, orphaned slots deleted: {outcome}");
            }
            else if (report.OrphanAccounts > 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Flat orphan storage check FAILED: {outcome} Set FlatDb.SweepOrphanStorage to delete them.");
            }
            else if (_logger.IsInfo) _logger.Info($"Flat orphan storage check passed: {outcome}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("The flat orphan storage sweep failed; it resumes from its cursor on the next start.", e);
        }
    }

    private void Delete(IReadOnlyKeyValueStore accounts, List<ValueHash256> orphans, CancellationToken token)
    {
        for (int first = 0; first < orphans.Count; first += IdentitiesPerBatch)
        {
            token.ThrowIfCancellationRequested();
            int last = Math.Min(orphans.Count, first + IdentitiesPerBatch);
            persistenceManager.RunMaintenance(batch =>
            {
                for (int index = first; index < last; index++)
                {
                    ValueHash256 identity = orphans[index];
                    if (!IsOrphan(accounts, identity, out _)) continue;

                    batch.DeleteStorageRange(identity, ValueKeccak.Zero, ValueKeccak.MaxValue);
                    batch.DeleteStorageTrieNodeRange(identity, ValueKeccak.Zero, ValueKeccak.MaxValue);
                }
            }, token);
        }
    }

    private void LogProgress(long prefix)
    {
        if (!_logger.IsInfo) return;
        if (_lastProgressAt != 0 && Stopwatch.GetElapsedTime(_lastProgressAt) < ProgressInterval) return;

        _lastProgressAt = Stopwatch.GetTimestamp();
        _logger.Info($"Flat orphan storage sweep at {prefix / (double)(1L << 32):P1} of the key space: {_slotsScanned:N0} slots scanned, {_orphanAccounts:N0} orphaned accounts so far.");
    }

    private long ReadCursor()
    {
        byte[]? value = db.GetColumnDb(FlatDbColumns.Metadata).Get(CursorKey);
        return value is { Length: sizeof(uint) } ? BinaryPrimitives.ReadUInt32BigEndian(value) : 0;
    }

    private void WriteCursor(uint prefix)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(value, prefix);
        db.GetColumnDb(FlatDbColumns.Metadata).PutSpan(CursorKey, value);
    }

    private static ValueHash256 IdentityOf(ReadOnlySpan<byte> storageKey)
    {
        Span<byte> identity = stackalloc byte[Hash256.Size];
        storageKey[..SlotOffset].CopyTo(identity);
        storageKey[SuffixOffset..StorageKeyLength].CopyTo(identity[SlotOffset..]);
        return new ValueHash256(identity);
    }

    private static bool IsOrphan(IReadOnlyKeyValueStore accounts, in ValueHash256 identity, out bool missing)
    {
        Span<byte> buffer = stackalloc byte[AccountBuffer];
        int length = accounts.Get(identity.Bytes[..IdentityLength], buffer);
        missing = length <= 0;
        if (missing) return true;

        RlpReader reader = new(buffer[..length]);
        return AccountDecoder.Slim.DecodeStorageRootOnly(ref reader) == Keccak.EmptyTreeHash;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _loop?.Join();
        _cts.Dispose();
    }
}

public readonly record struct OrphanStorageReport(long SlotsScanned, long OrphanSlots, long OrphanAccounts, long MissingAccounts, long EmptyRootAccounts);
