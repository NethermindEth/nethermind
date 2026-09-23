// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using Columns = Nethermind.State.Flat.History.Changesets.BulkFillScratchState.Columns;

namespace Nethermind.State.Flat.History.Changesets;

internal static class BulkFillStorageCleanup
{
    /// <summary>Each account's slot deletions and the removal of its clear marker go in one batch, so a crash leaves
    /// either the marker with the slots or neither. That is what makes a single WAL sync at the end enough: a batch
    /// the crash loses is redone from its marker on the next run, and syncing per account would cost an fsync for
    /// every one of them.</summary>
    public static void Run(IColumnsDb<Columns> db, CancellationToken token)
    {
        Span<byte> upper = stackalloc byte[Hash256.Size + 1];
        upper.Fill(0xFF);
        bool cleaned = false;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ValueHash256 address;
                ulong clearedAt;
                using (ISortedView clears = ((ISortedKeyValueStore)db.GetColumnDb(Columns.Clears)).GetViewBetween([], upper))
                {
                    if (!clears.MoveNext()) return;
                    if (clears.CurrentKey.Length != Hash256.Size || clears.CurrentValue.Length != sizeof(ulong))
                        throw new InvalidDataException("Invalid scratch clear during cleanup.");
                    address = new ValueHash256(clears.CurrentKey);
                    clearedAt = BinaryPrimitives.ReadUInt64BigEndian(clears.CurrentValue);
                }
                CleanAccount(db, address, clearedAt, token);
                cleaned = true;
            }
        }
        finally
        {
            if (cleaned) db.SyncWal();
        }
    }

    private static void CleanAccount(IColumnsDb<Columns> db, in ValueHash256 address, ulong clearedAt, CancellationToken token)
    {
        byte[]? account = db.GetColumnDb(Columns.Accounts)[address.Bytes];
        bool isDeleted = account is null || account.Length == 0;
        Span<byte> lower = stackalloc byte[BaseFlatPersistence.StorageKeyLength + 1];
        lower.Clear();
        address.Bytes[..HistoryKeyLayout.ScopeKeyLength].CopyTo(lower);
        Span<byte> upper = stackalloc byte[BaseFlatPersistence.StorageKeyLength + 1];
        upper.Fill(0xFF);
        address.Bytes[..HistoryKeyLayout.ScopeKeyLength].CopyTo(upper);
        int lowerLength = BaseFlatPersistence.StorageKeyLength;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            IColumnsWriteBatch<Columns> batch = db.StartWriteBatch();
            bool complete = false;
            try
            {
                using ISortedView slots = ((ISortedKeyValueStore)db.GetColumnDb(Columns.Storage)).GetViewBetween(lower[..lowerLength], upper, ReadFlags.HintReadAhead);
                for (int count = 0; count < 1024; count++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!slots.MoveNext())
                    {
                        complete = true;
                        break;
                    }
                    if (slots.CurrentKey.Length != BaseFlatPersistence.StorageKeyLength || slots.CurrentValue.Length < sizeof(ulong))
                        throw new InvalidDataException("Invalid scratch slot during cleanup.");
                    if (isDeleted || BinaryPrimitives.ReadUInt64BigEndian(slots.CurrentValue) < clearedAt)
                        batch.GetColumnBatch(Columns.Storage).Remove(slots.CurrentKey);
                    slots.CurrentKey.CopyTo(lower);
                    lower[^1] = 0;
                    lowerLength = lower.Length;
                }
                if (complete) batch.GetColumnBatch(Columns.Clears).Remove(address.Bytes);
            }
            catch
            {
                try
                {
                    batch.Clear();
                }
                finally
                {
                    batch.Dispose();
                }
                throw;
            }
            batch.Dispose();
            if (complete) return;
        }
    }
}
