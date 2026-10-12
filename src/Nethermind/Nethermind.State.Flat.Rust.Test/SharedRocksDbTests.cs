// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>The native store and C# on one RocksDB instance, opened by C# the way the flat DB is.</summary>
[Parallelizable(ParallelScope.All)]
public class SharedRocksDbTests
{
    [Test]
    public void Rust_and_csharp_read_each_others_writes([Values] FlatDbColumns column)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        using TrackingColumnsDb db = Open(path);
        using RustFlatStore store = RustFlatStore.Create(db, new FlatDbConfig());

        byte[] rustKey = Bytes.FromHexString("0x01aa");
        byte[] rustValue = Bytes.FromHexString("0xc0ffee");
        byte[] csharpKey = Bytes.FromHexString("0x02bb");
        // Longer than the first read buffer, so the read retries with a larger one.
        byte[] csharpValue = Enumerable.Range(0, 300).Select(static i => (byte)i).ToArray();
        FlatDbColumns otherColumn = column == FlatDbColumns.Metadata ? FlatDbColumns.Account : FlatDbColumns.Metadata;

        store.DebugPut(column, rustKey, rustValue);
        db.GetColumnDb(column).Set(csharpKey, csharpValue);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.GetColumnDb(column).Get(rustKey), Is.EqualTo(rustValue));
            Assert.That(store.DebugGet(column, csharpKey), Is.EqualTo(csharpValue));
            Assert.That(store.DebugGet(column, Bytes.FromHexString("0x03cc")), Is.Null);
            Assert.That(db.GetColumnDb(otherColumn).Get(rustKey), Is.Null);
        }
    }

    [Test]
    public void Native_reads_count_in_the_db_metrics_once_published()
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        using TrackingColumnsDb db = Open(path);
        using RustFlatStore store = RustFlatStore.Create(db, new FlatDbConfig());
        long before = db.GatherMetric().TotalReads;

        store.DebugGet(FlatDbColumns.Account, Bytes.FromHexString("0x01"));
        store.DebugGet(FlatDbColumns.Storage, Bytes.FromHexString("0x02"));
        long unpublished = db.GatherMetric().TotalReads;
        store.PublishReadMetrics();
        long published = db.GatherMetric().TotalReads;
        store.PublishReadMetrics();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unpublished, Is.EqualTo(before));
            Assert.That(published - before, Is.EqualTo(2));
            Assert.That(db.GatherMetric().TotalReads, Is.EqualTo(published), "reads are published once");
        }
    }

    [Test]
    public void Disposing_the_db_waits_for_the_store_or_leaves_it_the_close([Values] bool storeOutlivesDrainTimeout)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        TrackingColumnsDb db = Open(path, storeOutlivesDrainTimeout ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(10));
        RustFlatStore store = RustFlatStore.Create(db, new FlatDbConfig());
        store.DebugPut(FlatDbColumns.Account, Bytes.FromHexString("0x01"), Bytes.FromHexString("0x02"));

        Task disposing = Task.Run(db.Dispose);
        if (storeOutlivesDrainTimeout)
        {
            Assert.That(disposing.Wait(TimeSpan.FromSeconds(5)), "dispose waited past its drain timeout");
            Assert.That(db.NativeCloses, Is.Zero, "the database closed under the store");
        }
        else
        {
            Assert.That(disposing.Wait(TimeSpan.FromMilliseconds(200)), Is.False, "the database closed under the store");
        }

        store.Dispose();

        // Finishing well within the 10 s drain timeout proves the store released its lease.
        Assert.That(disposing.Wait(TimeSpan.FromSeconds(5)), "dispose never finished after the store was released");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.NativeCloses, Is.EqualTo(1), "the database must close exactly once");
            Assert.That(db.AcquireNativeLease, Throws.InstanceOf<ObjectDisposedException>());
        }
    }

    [Test]
    public void Store_needs_a_rocksdb_database()
    {
        RustNativeLibrary.Require();
        Assert.That(() => RustFlatStore.Create(new MemColumnsDb<FlatDbColumns>(), new FlatDbConfig()), Throws.InstanceOf<NotSupportedException>());
    }

    private static TrackingColumnsDb Open(TempPath path, TimeSpan? pinnedSliceDrainTimeout = null)
    {
        DbConfig dbConfig = new();
        RocksDbConfigFactory configFactory = new(dbConfig, new PruningConfig(), new TestHardwareInfo(1.GiB), LimboLogs.Instance, validateConfig: false);
        return new TrackingColumnsDb(path.Path, new DbSettings(nameof(DbNames.Flat), DbNames.Flat), dbConfig, configFactory)
        {
            PinnedSliceDrainTimeout = pinnedSliceDrainTimeout ?? TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>Counts native closes, to tell a close deferred to the last lease release from one done by dispose.</summary>
    private sealed class TrackingColumnsDb(string basePath, DbSettings settings, IDbConfig dbConfig, IRocksDbConfigFactory configFactory)
        : ColumnsDb<FlatDbColumns>(basePath, settings, dbConfig, configFactory, LimboLogs.Instance, [])
    {
        private int _nativeCloses;

        public int NativeCloses => Volatile.Read(ref _nativeCloses);

        protected override void ReleaseUnmanagedResources()
        {
            Interlocked.Increment(ref _nativeCloses);
            base.ReleaseUnmanagedResources();
        }
    }
}
