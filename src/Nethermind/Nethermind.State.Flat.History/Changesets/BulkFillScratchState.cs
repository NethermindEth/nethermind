// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Stages a historical import in a caller-owned database, separate from the live state.</summary>
/// <remarks>
/// Single writer only. The caller binds the identity to the source, chain, range and encodings, and protects
/// source retention while scanning. Imported rows are not a verified replay base.
/// </remarks>
internal sealed class BulkFillScratchState
{
    private const byte FormatVersion = 2;
    private static ReadOnlySpan<byte> IdentityKey => "bulk-fill-identity"u8;
    private readonly IColumnsDb<Columns> _db;
    private readonly ulong _anchor;
    private bool _isFaulted;

    internal enum Columns
    {
        Metadata,
        Accounts,
        Storage,
        Clears,
        Code,
    }

    public BulkFillScratchState(IColumnsDb<Columns> db, Hash256 identity, ulong anchor)
    {
        _db = db;
        _anchor = anchor;
        Span<byte> manifest = stackalloc byte[1 + Hash256.Size + sizeof(ulong)];
        manifest[0] = FormatVersion;
        identity.Bytes.CopyTo(manifest[1..]);
        BinaryPrimitives.WriteUInt64BigEndian(manifest[(1 + Hash256.Size)..], anchor);
        IDb metadata = db.GetColumnDb(Columns.Metadata);
        byte[]? stored = metadata[IdentityKey];
        if (stored is not null)
        {
            if (!manifest.SequenceEqual(stored)) throw new InvalidDataException("Scratch state belongs to a different bulk import.");
            db.SyncWal();
            return;
        }

        foreach (Columns column in Enum.GetValues<Columns>())
        {
            using IEnumerator<byte[]> keys = db.GetColumnDb(column).GetAllKeys(ordered: false).GetEnumerator();
            if (keys.MoveNext()) throw new InvalidDataException("Refusing to initialize a nonempty scratch database without an identity.");
        }

        metadata.PutSpan(IdentityKey, manifest);
        db.SyncWal();
    }

    /// <summary>Atomically writes a bounded scan page and its resume cursor, then syncs their shared WAL.</summary>
    /// <remarks>A failure requires reopening the store before another attempt. Callback writes are discarded if scanning fails.</remarks>
    public HistoricalStateScan.Page ImportPage(ISortedKeyValueStore source, HistoryRowFormat format,
        FlatHistoryColumns column, int maxRows, CancellationToken token)
    {
        if (_isFaulted) throw new InvalidOperationException("Reopen the scratch state after a failed import write.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        token.ThrowIfCancellationRequested();
        Columns target = TargetColumn(column);
        HistoricalStateScan scan = new(source, format, column, _anchor);
        byte[] checkpointKey = [(byte)target];
        byte[]? checkpoint = _db.GetColumnDb(Columns.Metadata)[checkpointKey];
        HistoricalStateScan.Cursor? cursor = null;
        if (checkpoint is not null)
        {
            int rowKeyLength = (column == FlatHistoryColumns.StorageHistory ? BaseFlatPersistence.StorageKeyLength : Hash256.Size) + sizeof(ulong);
            if (checkpoint.Length != 1 && checkpoint.Length != 1 + rowKeyLength || checkpoint[0] > 1)
                throw new InvalidDataException($"Invalid scratch import checkpoint for {column}.");
            if (checkpoint.Length > 1) cursor = new HistoricalStateScan.Cursor(column, _anchor, checkpoint.AsSpan(1));
            if (checkpoint[0] == 1) return new HistoricalStateScan.Page(cursor, 0, Complete: true);
            if (cursor is null) throw new InvalidDataException($"Incomplete scratch import checkpoint for {column} has no position.");
        }

        IColumnsWriteBatch<Columns> batch = _db.StartWriteBatch();
        HistoricalStateScan.Page page;
        try
        {
            IWriteBatch rows = batch.GetColumnBatch(target);
            page = scan.ReadPage(cursor, maxRows, (key, height, value) => Stage(rows, target, key, height, value), token);
            byte[] next = new byte[1 + (page.Position?.Key.Length ?? 0)];
            next[0] = page.Complete ? (byte)1 : (byte)0;
            if (page.Position is { } position) position.Key.CopyTo(next.AsSpan(1));
            batch.GetColumnBatch(Columns.Metadata).PutSpan(checkpointKey, next);
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            _isFaulted = true;
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

        try
        {
            batch.Dispose();
            _db.SyncWal();
        }
        catch
        {
            _isFaulted = true;
            throw;
        }
        return page;
    }

    public void VerifyAnchor(Hash256 expectedRoot, bool rlpWrappedSlots, CancellationToken token)
    {
        if (_isFaulted) throw new InvalidOperationException("Reopen the scratch state after a failed import write.");
        new BulkFillScratchVerifier(_db, _anchor).VerifyAnchor(expectedRoot, rlpWrappedSlots, token);
    }

    public double ImportFraction(FlatHistoryColumns column)
    {
        byte[]? checkpoint = _db.GetColumnDb(Columns.Metadata)[new byte[] { (byte)TargetColumn(column) }];
        return checkpoint is null ? 0 : checkpoint[0] == 1 ? 1 : BulkFillImportProgress.Fraction(checkpoint.AsSpan(1));
    }

    private static Columns TargetColumn(FlatHistoryColumns column) => column switch
    {
        FlatHistoryColumns.AccountHistory => Columns.Accounts,
        FlatHistoryColumns.StorageHistory => Columns.Storage,
        FlatHistoryColumns.StorageClears => Columns.Clears,
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    private static void Stage(IWriteBatch batch, Columns target, ReadOnlySpan<byte> key, ulong height, ReadOnlySpan<byte> value)
    {
        if (target == Columns.Accounts)
        {
            batch.PutSpan(key, value);
            return;
        }

        Span<byte> record = stackalloc byte[sizeof(ulong) + BaseFlatPersistence.RlpSlotValueBufferSize];
        BinaryPrimitives.WriteUInt64BigEndian(record, height);
        value.CopyTo(record[sizeof(ulong)..]);
        if (target == Columns.Storage)
        {
            Span<byte> orderedKey = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
            HistoryKeyLayout.Storage.ExtractAddressKey(key, orderedKey);
            key.Slice(BasePersistence.StoragePrefixPortion, Hash256.Size).CopyTo(orderedKey[HistoryKeyLayout.ScopeKeyLength..]);
            batch.PutSpan(orderedKey, record[..(sizeof(ulong) + value.Length)]);
        }
        else
        {
            batch.PutSpan(key, record[..sizeof(ulong)]);
        }
    }
}
