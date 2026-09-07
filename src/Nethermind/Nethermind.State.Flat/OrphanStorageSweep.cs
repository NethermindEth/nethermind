// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

public sealed class OrphanStorageSweep(IColumnsDb<FlatDbColumns> db, IPersistence persistence, ILogManager logManager)
{
    private static readonly byte[] SweptKey = Keccak.Compute("OrphanStorageSwept").BytesToArray();
    private const byte SweepVersion = 1;
    private const int IdentitiesPerBatch = 256;
    private const int IdentityLength = BaseFlatPersistence.AccountKeyLength;
    private const int StorageKeyLength = BaseFlatPersistence.StorageKeyLength;
    private const int SlotOffset = BasePersistence.StoragePrefixPortion;
    private const int SuffixOffset = SlotOffset + Hash256.Size;

    private readonly ILogger _logger = logManager.GetClassLogger<OrphanStorageSweep>();

    public bool AlreadySwept => db.GetColumnDb(FlatDbColumns.Metadata).Get(SweptKey) is { Length: > 0 };

    public OrphanStorageReport Sweep(bool repair, CancellationToken token)
    {
        ISortedKeyValueStore storage = (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage);
        IReadOnlyKeyValueStore accounts = db.GetColumnDb(FlatDbColumns.Account);
        Dictionary<ValueHash256, bool> decided = [];
        List<ValueHash256> orphans = [];
        long scanned = 0;
        long orphanSlots = 0;
        long missingAccounts = 0;
        long emptyRootAccounts = 0;
        int prefix = -1;

        Span<byte> upper = stackalloc byte[StorageKeyLength + 1];
        upper.Fill(0xFF);
        using (ISortedView view = storage.GetViewBetween(ReadOnlySpan<byte>.Empty, upper))
        {
            while (view.MoveNext())
            {
                token.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> key = view.CurrentKey;
                if (key.Length != StorageKeyLength) continue;

                scanned++;
                int keyPrefix = BinaryPrimitives.ReadInt32BigEndian(key);
                if (keyPrefix != prefix)
                {
                    prefix = keyPrefix;
                    decided.Clear();
                }

                ValueHash256 identity = IdentityOf(key);
                if (!decided.TryGetValue(identity, out bool orphan))
                {
                    orphan = IsOrphan(accounts, identity, ref missingAccounts, ref emptyRootAccounts);
                    decided[identity] = orphan;
                    if (orphan) orphans.Add(identity);
                }

                if (orphan) orphanSlots++;
                if (scanned % 50_000_000 == 0 && _logger.IsInfo) _logger.Info($"Orphan storage sweep: {scanned:N0} slots scanned, {orphans.Count:N0} orphaned accounts so far.");
            }
        }

        if (!repair) return new OrphanStorageReport(scanned, orphanSlots, orphans.Count, missingAccounts, emptyRootAccounts);

        for (int first = 0; first < orphans.Count; first += IdentitiesPerBatch)
        {
            token.ThrowIfCancellationRequested();
            using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync);
            int last = Math.Min(orphans.Count, first + IdentitiesPerBatch);
            for (int index = first; index < last; index++)
            {
                batch.DeleteStorageRange(orphans[index], ValueKeccak.Zero, ValueKeccak.MaxValue);
                batch.DeleteStorageTrieNodeRange(orphans[index], ValueKeccak.Zero, ValueKeccak.MaxValue);
            }
        }

        db.GetColumnDb(FlatDbColumns.Metadata).PutSpan(SweptKey, [SweepVersion]);
        return new OrphanStorageReport(scanned, orphanSlots, orphans.Count, missingAccounts, emptyRootAccounts);
    }

    private static ValueHash256 IdentityOf(ReadOnlySpan<byte> storageKey)
    {
        Span<byte> identity = stackalloc byte[Hash256.Size];
        storageKey[..SlotOffset].CopyTo(identity);
        storageKey[SuffixOffset..StorageKeyLength].CopyTo(identity[SlotOffset..]);
        return new ValueHash256(identity);
    }

    private static bool IsOrphan(IReadOnlyKeyValueStore accounts, in ValueHash256 identity, ref long missingAccounts, ref long emptyRootAccounts)
    {
        byte[]? raw = accounts.Get(identity.Bytes[..IdentityLength]);
        if (raw is null || raw.Length == 0)
        {
            missingAccounts++;
            return true;
        }

        RlpReader reader = new(raw);
        Account? account = AccountDecoder.Slim.Decode(ref reader);
        if (account is null || account.StorageRoot == Keccak.EmptyTreeHash)
        {
            emptyRootAccounts++;
            return true;
        }

        return false;
    }
}

public readonly record struct OrphanStorageReport(long SlotsScanned, long OrphanSlots, long OrphanAccounts, long MissingAccounts, long EmptyRootAccounts);
