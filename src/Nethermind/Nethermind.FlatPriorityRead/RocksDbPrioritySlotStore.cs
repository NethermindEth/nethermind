// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat;

namespace Nethermind.FlatPriorityRead;

/// <summary>An <see cref="IPrioritySlotStore"/> on its own RocksDB, wiped and rebuilt at every start.</summary>
/// <remarks>
/// <para>
/// Each reader holds a RocksDB snapshot. The table format is PlainTable (see <see cref="FlatPriorityReadRocksDbConfig"/>),
/// which supports neither range deletes nor ordered scans, so the store only ever issues point writes.
/// </para>
/// <para>
/// The first <see cref="BeginRead"/> ends the load phase. Until then nothing can see a commit, so writes skip the
/// WAL, which also lets the database split them into smaller writes. After that every commit is one atomic write
/// with the WAL on, so a snapshot never sees half of one. The first reader also compacts what the load wrote.
/// </para>
/// </remarks>
public sealed class RocksDbPrioritySlotStore : IPrioritySlotStore
{
    public const string DbName = "FlatPriorityRead";

    private readonly Lock _loadLock = new();
    private readonly IDb _db;
    private readonly IKeyValueStoreWithSnapshot _snapshots;
    private bool _loaded;

    public RocksDbPrioritySlotStore(IDbFactory dbFactory, IFlatPriorityReadConfig config)
    {
        _db = dbFactory.CreateDb(new DbSettings(DbName, Path.Combine(config.DbPath, "rocksdb")) { DeleteOnStart = true });
        _snapshots = _db as IKeyValueStoreWithSnapshot
            ?? throw new NotSupportedException($"The {DbName} database must support snapshots; {_db.GetType().Name} does not.");
    }

    public IPrioritySlotStore.IReader BeginRead()
    {
        if (!Volatile.Read(ref _loaded)) EndLoad();
        return new Reader(_snapshots.CreateSnapshot());
    }

    public IPrioritySlotStore.IWriter BeginWrite() =>
        new Writer(_db.StartWriteBatch(), Volatile.Read(ref _loaded) ? WriteFlags.None : WriteFlags.DisableWAL);

    public void Dispose() => _db.Dispose();

    private void EndLoad()
    {
        using Lock.Scope _ = _loadLock.EnterScope();
        if (_loaded) return;

        _db.Compact();
        Volatile.Write(ref _loaded, true);
    }

    private sealed class Reader : IPrioritySlotStore.IReader
    {
        private readonly IKeyValueStoreSnapshot _snapshot;

        public Reader(IKeyValueStoreSnapshot snapshot)
        {
            _snapshot = snapshot;
            Span<byte> state = stackalloc byte[PrioritySlotEncoding.StateLength];
            State = snapshot.Get(PrioritySlotEncoding.StateKey, state) == PrioritySlotEncoding.StateLength
                ? PrioritySlotEncoding.ReadState(state)
                : default;
        }

        public StateId State { get; }

        public bool TryGet(byte contract, in ValueHash256 slotKey, out UInt256 value)
        {
            Span<byte> key = stackalloc byte[PrioritySlotEncoding.KeyLength];
            PrioritySlotEncoding.WriteKey(key, contract, slotKey);
            Span<byte> buffer = stackalloc byte[PrioritySlotEncoding.ValueLength];
            if (_snapshot.Get(key, buffer) != PrioritySlotEncoding.ValueLength)
            {
                value = default;
                return false;
            }

            value = PrioritySlotEncoding.ReadValue(buffer);
            return true;
        }

        public void Dispose() => _snapshot.Dispose();
    }

    private sealed class Writer(IWriteBatch batch, WriteFlags flags) : IPrioritySlotStore.IWriter
    {
        private bool _committed;

        public void Set(byte contract, in ValueHash256 slotKey, in UInt256 value)
        {
            Span<byte> key = stackalloc byte[PrioritySlotEncoding.KeyLength];
            PrioritySlotEncoding.WriteKey(key, contract, slotKey);
            Span<byte> encoded = stackalloc byte[PrioritySlotEncoding.ValueLength];
            PrioritySlotEncoding.WriteValue(encoded, value);
            batch.PutSpan(key, encoded, flags);
        }

        public void Remove(byte contract, in ValueHash256 slotKey)
        {
            Span<byte> key = stackalloc byte[PrioritySlotEncoding.KeyLength];
            PrioritySlotEncoding.WriteKey(key, contract, slotKey);
            batch.Set(key, null, flags);
        }

        public void Commit(in StateId state)
        {
            Span<byte> encoded = stackalloc byte[PrioritySlotEncoding.StateLength];
            PrioritySlotEncoding.WriteState(encoded, state);
            batch.PutSpan(PrioritySlotEncoding.StateKey, encoded, flags);
            batch.Dispose();
            _committed = true;
        }

        public void Dispose()
        {
            if (_committed) return;

            // Disposing a batch writes it, so an abandoned one is emptied first.
            batch.Clear();
            batch.Dispose();
        }
    }
}
