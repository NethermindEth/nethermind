// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>The flat-column databases and C# persistence stacks the tests compare the Rust persistence with.</summary>
public static class FlatTestDbs
{
    /// <summary>The database a test runs on.</summary>
    public enum Kind
    {
        /// <summary>A RocksDB <see cref="ColumnsDb{T}"/>, opened as the node opens the flat DB; the store drives it natively.</summary>
        RocksDb,

        /// <summary>The in-memory columns of the DI test suites, reached through the managed key-value backend.</summary>
        Memory,
    }

    /// <summary>The metadata key BasePersistence.ResolveSlotEncoding reads; the value is 0 for raw, 1 for RLP.</summary>
    public static readonly byte[] SlotEncodingKey = Keccak.Compute("SlotEncoding").BytesToArray();

    public static IColumnsDb<FlatDbColumns> Open(Kind kind, string path) => kind switch
    {
        Kind.RocksDb => OpenRocksDb(path),
        _ => new SnapshotableMemColumnsDb<FlatDbColumns>(),
    };

    public static ColumnsDb<FlatDbColumns> OpenRocksDb(string path) => new TrackingColumnsDb(path);

    /// <summary>The persisted-snapshot catalog in <paramref name="basePath"/>, opened as <c>FlatWorldStateModule</c> opens it.</summary>
    public static DbOnTheRocks OpenRocksDbCatalog(string basePath)
    {
        DbConfig dbConfig = new();
        return new DbOnTheRocks(basePath, new DbSettings(nameof(DbNames.PersistedSnapshotCatalog), Path.Combine("persistedSnapshot", "catalog")), dbConfig,
            new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(1.GiB), LimboLogs.Instance, validateConfig: false), LimboLogs.Instance);
    }

    /// <summary>Forces the slot encoding the persistence resolves when it opens the database.</summary>
    public static void ForceSlotEncoding(IColumnsDb<FlatDbColumns> db, bool rlpSlots) =>
        db.GetColumnDb(FlatDbColumns.Metadata).Set(SlotEncodingKey, [rlpSlots ? (byte)1 : (byte)0]);

    /// <summary>The C# stack FlatWorldStateModule registers as <see cref="IPersistence"/>.</summary>
    public static IPersistence CreateCSharpStack(IColumnsDb<FlatDbColumns> db, FlatLayout layout, bool carryForward)
    {
        IPersistence layoutPersistence = layout switch
        {
            FlatLayout.Flat => new RocksDbPersistence(db, LimboLogs.Instance, new FlatDbConfig()),
            FlatLayout.FlatInTrie => new FlatInTriePersistence(db, LimboLogs.Instance),
            _ => new PreimageRocksdbPersistence(db, LimboLogs.Instance, layout),
        };
        IPersistence cachedReader = new CachedReaderPersistence(layoutPersistence, new ProcessExitSource(CancellationToken.None), LimboLogs.Instance);
        return carryForward ? new CarryForwardCachingPersistence(cachedReader) : cachedReader;
    }

    /// <summary>Every entry of <paramref name="column"/>, as <c>key=value</c> hex in key order.</summary>
    public static List<string> Dump(IColumnsDb<FlatDbColumns> db, FlatDbColumns column) =>
        [.. db.GetColumnDb(column).GetAll().Select(static entry => $"{entry.Key.ToHexString()}={entry.Value.ToHexString()}").Order(StringComparer.Ordinal)];

    /// <summary>Every entry of every column, as <c>column key=value</c>.</summary>
    public static List<string> DumpAll(IColumnsDb<FlatDbColumns> db) =>
        [.. Enum.GetValues<FlatDbColumns>().SelectMany(column => Dump(db, column).Select(entry => $"{column} {entry}"))];

    /// <summary>
    /// A flat DB that counts its native closes, to tell a close deferred to the last lease release from one done by
    /// dispose, and the fatal errors that would have shut the process down, which it only counts.
    /// </summary>
    public sealed class TrackingColumnsDb : ColumnsDb<FlatDbColumns>
    {
        private int _nativeCloses;
        private int _fatalShutdowns;

        public TrackingColumnsDb(string basePath)
            : this(basePath, TimeSpan.FromSeconds(10), LimboLogs.Instance)
        {
        }

        public TrackingColumnsDb(string basePath, TimeSpan pinnedSliceDrainTimeout, ILogManager logManager)
            : this(basePath, new DbConfig(), logManager) =>
            PinnedSliceDrainTimeout = pinnedSliceDrainTimeout;

        private TrackingColumnsDb(string basePath, DbConfig dbConfig, ILogManager logManager)
            : base(basePath, new DbSettings(nameof(DbNames.Flat), DbNames.Flat), dbConfig,
                new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(1.GiB), logManager, validateConfig: false),
                logManager, [])
        {
        }

        public int NativeCloses => Volatile.Read(ref _nativeCloses);

        /// <summary>How many fatal RocksDB errors <see cref="DbOnTheRocks.HandleFatalDbError"/> shut down on.</summary>
        public int FatalShutdowns => Volatile.Read(ref _fatalShutdowns);

        /// <summary>Runs as the native database closes.</summary>
        public Action? NativeClosing { get; set; }

        /// <summary>Runs as a fatal RocksDB error shuts down, before it is counted.</summary>
        public Action? FatalShuttingDown { get; set; }

        protected override void ReleaseUnmanagedResources()
        {
            Interlocked.Increment(ref _nativeCloses);
            NativeClosing?.Invoke();
            base.ReleaseUnmanagedResources();
        }

        protected override void FatalShutdown()
        {
            FatalShuttingDown?.Invoke();
            Interlocked.Increment(ref _fatalShutdowns);
        }
    }
}
