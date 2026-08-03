// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using LightningDB;

namespace Nethermind.Benchmarks.State.FlatBase;

/// <summary>
/// LMDB reference backend via the LightningDB binding: two named databases ("account", "storage")
/// in one environment. Referenced ONLY by this benchmark project — never from shipping projects.
/// </summary>
/// <remarks>
/// Writes append in globally ascending key order (shards are written in order, sorted within), so
/// <see cref="PutOptions.AppendData"/> bulk-loads the B-tree without page splits. Reads open the
/// environment with <c>MDB_NOTLS</c> (read transactions per benchmark worker, not per OS thread slot)
/// and <c>MDB_NORDAHEAD</c> (no OS readahead — the workload is uniform random point reads).
/// </remarks>
internal sealed class LmdbFlatBackend : IFlatPointReadBackend
{
    private readonly LightningEnvironment _env;
    private readonly LightningDatabase _accountDb;
    private readonly LightningDatabase _storageDb;

    private LmdbFlatBackend(LightningEnvironment env, LightningDatabase accountDb, LightningDatabase storageDb)
    {
        _env = env;
        _accountDb = accountDb;
        _storageDb = storageDb;
    }

    public static LmdbFlatBackend OpenWrite(string dir, long mapSize) =>
        Open(dir, mapSize, EnvironmentOpenFlags.None, new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create });

    public static LmdbFlatBackend OpenRead(string dir, long mapSize) =>
        Open(dir, mapSize,
            EnvironmentOpenFlags.ReadOnly | EnvironmentOpenFlags.NoThreadLocalStorage | EnvironmentOpenFlags.NoReadAhead,
            new DatabaseConfiguration());

    private static LmdbFlatBackend Open(string dir, long mapSize, EnvironmentOpenFlags flags, DatabaseConfiguration dbConfig)
    {
        Directory.CreateDirectory(dir);
        LightningEnvironment env = new(dir, new EnvironmentConfiguration
        {
            MapSize = mapSize,
            MaxDatabases = 2,
            MaxReaders = 512,
        });
        env.Open(flags);

        using LightningTransaction tx = env.BeginTransaction(
            flags.HasFlag(EnvironmentOpenFlags.ReadOnly) ? TransactionBeginFlags.ReadOnly : TransactionBeginFlags.None);
        LightningDatabase accountDb = tx.OpenDatabase("account", dbConfig);
        LightningDatabase storageDb = tx.OpenDatabase("storage", dbConfig);
        Check(tx.Commit());
        return new LmdbFlatBackend(env, accountDb, storageDb);
    }

    /// <summary>Bulk-load one shard. Keys must be sorted ascending and follow all previously written
    /// keys of the database (<see cref="PutOptions.AppendData"/>). One transaction per shard keeps the
    /// dirty-page list bounded on the full-scale dataset.</summary>
    public void PutShard(bool storage, byte[][] keys, byte[][] values, int count)
    {
        using LightningTransaction tx = _env.BeginTransaction();
        LightningDatabase db = storage ? _storageDb : _accountDb;
        for (int i = 0; i < count; i++)
            Check(tx.Put(db, keys[i], values[i], PutOptions.AppendData));
        Check(tx.Commit());
    }

    public IFlatReadSession BeginSession() => new Session(this);

    public void Dispose()
    {
        _accountDb.Dispose();
        _storageDb.Dispose();
        _env.Dispose();
    }

    private static void Check(MDBResultCode resultCode)
    {
        if (resultCode != MDBResultCode.Success)
            throw new InvalidOperationException($"LMDB operation failed: {resultCode}");
    }

    private sealed class Session(LmdbFlatBackend backend) : IFlatReadSession
    {
        private readonly LmdbFlatBackend _backend = backend;
        private readonly LightningTransaction _tx = backend._env.BeginTransaction(TransactionBeginFlags.ReadOnly);

        public int GetAccount(ReadOnlySpan<byte> key20, Span<byte> valueOut) =>
            Get(_backend._accountDb, key20, valueOut);

        public int GetSlot(ReadOnlySpan<byte> key52, Span<byte> valueOut) =>
            Get(_backend._storageDb, key52, valueOut);

        private int Get(LightningDatabase db, ReadOnlySpan<byte> key, Span<byte> valueOut)
        {
            (MDBResultCode resultCode, MDBValue _, MDBValue value) = _tx.Get(db, key);
            if (resultCode != MDBResultCode.Success) return 0;

            ReadOnlySpan<byte> span = value.AsSpan();
            span.CopyTo(valueOut);
            return span.Length;
        }

        public void Dispose() => _tx.Dispose();
    }
}
