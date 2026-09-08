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

public sealed class OrphanStorageSweep(IColumnsDb<FlatDbColumns> db, IPersistenceManager persistenceManager, SweepPacer pacer, ILogManager logManager)
    : PacedSweep(db, "OrphanStorageSwept", "OrphanStorageSweepProgress", TallyLength, pacer, logManager.GetClassLogger<OrphanStorageSweep>())
{
    private const int SlotsScanned = 0;
    private const int OrphanSlots = 1;
    private const int OrphanAccounts = 2;
    private const int MissingAccounts = 3;
    private const int EmptyRootAccounts = 4;
    private const int TallyLength = 5;
    private const int AccountBuffer = 256;
    private const int IdentityLength = BaseFlatPersistence.AccountKeyLength;
    private const int StorageKeyLength = BaseFlatPersistence.StorageKeyLength;
    private const int SlotOffset = BasePersistence.StoragePrefixPortion;
    private const int SuffixOffset = SlotOffset + Hash256.Size;
    private static readonly TimeSpan DeleteBudget = TimeSpan.FromMilliseconds(500);
    private bool _waitingLogged;

    public OrphanStorageReport Report => new(Tally[SlotsScanned], Tally[OrphanSlots], Tally[OrphanAccounts], Tally[MissingAccounts], Tally[EmptyRootAccounts]);

    internal new OrphanStorageReport RunToCompletion(bool repair, CancellationToken token)
    {
        base.RunToCompletion(repair, token);
        return Report;
    }

    protected override string Name(bool repair) => repair ? "Flat orphan storage sweep" : "Flat orphan storage check";

    protected override string StartDescription(bool repair) => repair
        ? "Flat orphan storage sweep starting in the background: every storage slot is checked against its account, and the slots of accounts that are absent or have an empty storage root are deleted. It pauses for as long as it runs, resumes across restarts, and stamps the database when done."
        : "Flat orphan storage check starting in the background: every storage slot is checked against its account; nothing is deleted.";

    protected override string Outcome()
    {
        OrphanStorageReport report = Report;
        return $"{report.SlotsScanned:N0} slots scanned, {report.OrphanSlots:N0} orphaned slots under {report.OrphanAccounts:N0} accounts ({report.MissingAccounts:N0} absent, {report.EmptyRootAccounts:N0} with an empty storage root)";
    }

    protected override string ProgressDetail() => $"{Tally[SlotsScanned]:N0} slots scanned, {Tally[OrphanAccounts]:N0} orphaned accounts so far";

    protected override bool FoundOrphans => Tally[OrphanAccounts] > 0;

    protected override bool Scan(bool repair, ReadOnlySpan<byte> start, long maxUnits, TimeSpan budget, out byte[]? next, CancellationToken token)
    {
        long startPrefix = start.Length == sizeof(uint) ? BinaryPrimitives.ReadUInt32BigEndian(start) : 0;
        List<ValueHash256> orphans = [];
        long nextPrefix;
        bool completed;
        using (IColumnDbSnapshot<FlatDbColumns> snapshot = Flat.CreateSnapshot())
        {
            completed = ScanSnapshot(repair, snapshot, startPrefix, maxUnits, budget, orphans, out nextPrefix, token);
        }

        if (repair && orphans.Count > 0)
        {
            WriteProgress(Cursor(startPrefix));
            Delete(Flat.GetColumnDb(FlatDbColumns.Account), orphans, token);
        }

        next = completed ? null : Cursor(nextPrefix);
        return completed;
    }

    private static byte[] Cursor(long prefix)
    {
        byte[] cursor = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(cursor, (uint)prefix);
        return cursor;
    }

    private bool ScanSnapshot(bool repair, IColumnDbSnapshot<FlatDbColumns> snapshot, long startPrefix, long maxUnits, TimeSpan budget, List<ValueHash256> orphans, out long nextPrefix, CancellationToken token)
    {
        ISortedKeyValueStore storage = (ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Storage);
        IReadOnlyKeyValueStore accounts = snapshot.GetColumn(FlatDbColumns.Account);
        Dictionary<ValueHash256, bool> decided = [];
        long passStartedAt = Stopwatch.GetTimestamp();
        long slotsThisPass = 0;
        long currentPrefix = -1;
        nextPrefix = 0;

        Span<byte> lower = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(lower, (uint)startPrefix);
        Span<byte> upper = stackalloc byte[StorageKeyLength + 1];
        upper.Fill(0xFF);
        using ISortedView view = storage.GetViewBetween(startPrefix == 0 ? ReadOnlySpan<byte>.Empty : lower, upper, ReadFlags.HintCacheMiss | ReadFlags.HintReadAhead);
        while (view.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != StorageKeyLength) continue;

            long prefix = BinaryPrimitives.ReadUInt32BigEndian(key);
            if (prefix != currentPrefix)
            {
                if (currentPrefix >= 0 && (slotsThisPass >= maxUnits || Stopwatch.GetElapsedTime(passStartedAt) >= budget))
                {
                    nextPrefix = prefix;
                    return false;
                }

                currentPrefix = prefix;
                decided.Clear();
                LogProgress(repair, key);
            }

            slotsThisPass++;
            Tally[SlotsScanned]++;
            ValueHash256 identity = IdentityOf(key);
            if (!decided.TryGetValue(identity, out bool orphan))
            {
                orphan = IsOrphan(accounts, identity, out bool missing);
                decided[identity] = orphan;
                if (orphan)
                {
                    Tally[OrphanAccounts]++;
                    if (missing) Tally[MissingAccounts]++;
                    else Tally[EmptyRootAccounts]++;
                    if (repair) orphans.Add(identity);
                }
            }

            if (orphan) Tally[OrphanSlots]++;
        }

        return true;
    }

    private void Delete(IReadOnlyKeyValueStore accounts, List<ValueHash256> orphans, CancellationToken token)
    {
        int next = 0;
        while (next < orphans.Count)
        {
            token.ThrowIfCancellationRequested();
            bool applied = persistenceManager.RunMaintenance(batch =>
            {
                long startedAt = Stopwatch.GetTimestamp();
                while (next < orphans.Count && !token.IsCancellationRequested && Stopwatch.GetElapsedTime(startedAt) < DeleteBudget)
                {
                    ValueHash256 identity = orphans[next++];
                    if (!IsOrphan(accounts, identity, out _)) continue;

                    batch.DeleteStorageRange(identity, ValueKeccak.Zero, ValueKeccak.MaxValue);
                    batch.DeleteStorageTrieNodeRange(identity, ValueKeccak.Zero, ValueKeccak.MaxValue);
                }
            }, token);

            if (!applied) WaitForStateSync(token);
        }
    }

    private void WaitForStateSync(CancellationToken token)
    {
        if (!Drained(0))
        {
            StampSwept();
            if (Logger.IsWarn) Logger.Warn("Flat orphan storage sweep stopped: the flat database was cleared under it and a state sync is rebuilding it from scratch. Everything that sync writes is written by this version and cannot be orphaned, so the database is recorded as swept.");
            throw new OperationCanceledException();
        }

        if (!_waitingLogged)
        {
            _waitingLogged = true;
            if (Logger.IsInfo) Logger.Info("Flat orphan storage sweep is waiting: a state sync is writing to the flat database, and nothing in it may be judged until that sync has finished.");
        }

        if (token.WaitHandle.WaitOne(RetryPoll)) throw new OperationCanceledException(token);
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
        int length = accounts.Get(identity.Bytes[..IdentityLength], buffer, ReadFlags.HintCacheMiss);
        missing = length <= 0;
        if (missing) return true;

        RlpReader reader = new(buffer[..length]);
        return AccountDecoder.Slim.DecodeStorageRootOnly(ref reader) == Keccak.EmptyTreeHash;
    }
}

public readonly record struct OrphanStorageReport(long SlotsScanned, long OrphanSlots, long OrphanAccounts, long MissingAccounts, long EmptyRootAccounts);
