// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using LightningDB;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat;

namespace Nethermind.FlatPriorityRead;

/// <summary>An <see cref="IPrioritySlotStore"/> on LMDB, wiped and rebuilt at every start.</summary>
/// <remarks>
/// Each reader holds one read transaction, which is LMDB's snapshot. LMDB lets only one thread at a time use a
/// transaction, so a reader serializes its lookups. The environment never syncs: the store is rebuilt on restart.
/// </remarks>
public sealed class LmdbPrioritySlotStore : IPrioritySlotStore
{
    private const EnvironmentOpenFlags OpenFlags =
        EnvironmentOpenFlags.NoSync |
        EnvironmentOpenFlags.NoMetaSync |
        EnvironmentOpenFlags.WriteMap |
        EnvironmentOpenFlags.NoThreadLocalStorage |
        EnvironmentOpenFlags.NoReadAhead;

    private readonly LightningEnvironment _environment;
    private readonly LightningDatabase _database;

    public LmdbPrioritySlotStore(IFlatPriorityReadConfig config)
    {
        string path = Path.Combine(config.DbPath, "lmdb");
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);

        _environment = new LightningEnvironment(path, new EnvironmentConfiguration
        {
            MapSize = config.LmdbMapSize,
            MaxReaders = config.LmdbMaxReaders,
        });
        _environment.Open(OpenFlags);

        using LightningTransaction transaction = _environment.BeginTransaction();
        _database = transaction.OpenDatabase(configuration: new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create });
        transaction.Commit().ThrowOnError();
    }

    public IPrioritySlotStore.IReader BeginRead() => new Reader(_environment.BeginTransaction(TransactionBeginFlags.ReadOnly), _database);

    public IPrioritySlotStore.IWriter BeginWrite() => new Writer(_environment.BeginTransaction(), _database);

    public void Dispose()
    {
        _database.Dispose();
        _environment.Dispose();
    }

    private sealed class Reader : IPrioritySlotStore.IReader
    {
        private readonly Lock _lock = new();
        private readonly LightningTransaction _transaction;
        private readonly LightningDatabase _database;

        public Reader(LightningTransaction transaction, LightningDatabase database)
        {
            _transaction = transaction;
            _database = database;

            Span<byte> state = stackalloc byte[PrioritySlotEncoding.StateLength];
            State = transaction.TryGet(database, PrioritySlotEncoding.StateKey, state, out _)
                ? PrioritySlotEncoding.ReadState(state)
                : default;
        }

        public StateId State { get; }

        public bool TryGet(byte contract, in ValueHash256 slotKey, out UInt256 value)
        {
            Span<byte> key = stackalloc byte[PrioritySlotEncoding.KeyLength];
            PrioritySlotEncoding.WriteKey(key, contract, slotKey);
            Span<byte> buffer = stackalloc byte[PrioritySlotEncoding.ValueLength];

            bool found;
            using (_lock.EnterScope())
            {
                found = _transaction.TryGet(_database, key, buffer, out _);
            }

            value = found ? PrioritySlotEncoding.ReadValue(buffer) : default;
            return found;
        }

        public void Dispose()
        {
            using Lock.Scope _ = _lock.EnterScope();
            _transaction.Dispose();
        }
    }

    private sealed class Writer(LightningTransaction transaction, LightningDatabase database) : IPrioritySlotStore.IWriter
    {
        public void Set(byte contract, in ValueHash256 slotKey, in UInt256 value)
        {
            Span<byte> key = stackalloc byte[PrioritySlotEncoding.KeyLength];
            PrioritySlotEncoding.WriteKey(key, contract, slotKey);
            Span<byte> encoded = stackalloc byte[PrioritySlotEncoding.ValueLength];
            PrioritySlotEncoding.WriteValue(encoded, value);
            transaction.Put(database, key, encoded).ThrowOnError();
        }

        public void Remove(byte contract, in ValueHash256 slotKey)
        {
            Span<byte> key = stackalloc byte[PrioritySlotEncoding.KeyLength];
            PrioritySlotEncoding.WriteKey(key, contract, slotKey);
            MDBResultCode result = transaction.Delete(database, key);
            if (result != MDBResultCode.NotFound) result.ThrowOnError();
        }

        public void Commit(in StateId state)
        {
            Span<byte> encoded = stackalloc byte[PrioritySlotEncoding.StateLength];
            PrioritySlotEncoding.WriteState(encoded, state);
            transaction.Put(database, PrioritySlotEncoding.StateKey, encoded).ThrowOnError();
            transaction.Commit().ThrowOnError();
        }

        // Aborts the transaction when it was not committed.
        public void Dispose() => transaction.Dispose();
    }
}
