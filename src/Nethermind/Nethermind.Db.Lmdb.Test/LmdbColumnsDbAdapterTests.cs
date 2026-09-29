// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.State.Flat;
using NUnit.Framework;

namespace Nethermind.Db.Lmdb.Test;

public class LmdbColumnsDbAdapterTests
{
    private const long TestMapSize = 64L * 1024 * 1024;
    private const int TestMaxReaders = 8192;
    private const int TestMaxOperationsPerWave = 4096;

    private string DbPath => Path.Combine("testdb-lmdb-columns", TestContext.CurrentContext.Test.ID);
    private SnapshotableMemColumnsDb<FlatDbColumns> _inner = null!;
    private LmdbColumnsDbAdapter _adapter = null!;

    [SetUp]
    public void Setup()
    {
        if (Directory.Exists(DbPath)) Directory.Delete(DbPath, true);
        // Snapshot-capable, unlike MemColumnsDb, so CreateSnapshot tests exercise the same shape of inner
        // columns DB that LmdbDbFactoryDecorator wraps in production (a real RocksDB ColumnsDb<T>).
        _inner = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _adapter = new LmdbColumnsDbAdapter(
            _inner,
            new LmdbDb("account", Path.Combine(DbPath, "account.lmdb"), TestMapSize, TestMaxReaders, TestMaxOperationsPerWave),
            new LmdbDb("storage", Path.Combine(DbPath, "storage.lmdb"), TestMapSize, TestMaxReaders, TestMaxOperationsPerWave),
            LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _adapter.Dispose();
        _inner.Dispose();
        if (Directory.Exists(DbPath)) Directory.Delete(DbPath, true);
    }

    [Test]
    public void Account_and_storage_columns_are_backed_by_Lmdb()
    {
        Assert.That(_adapter.GetColumnDb(FlatDbColumns.Account), Is.TypeOf<LmdbDb>());
        Assert.That(_adapter.GetColumnDb(FlatDbColumns.Storage), Is.TypeOf<LmdbDb>());
    }

    [Test]
    public void Every_other_column_is_delegated_to_the_inner_columns_db_unchanged()
    {
        Assert.That(_adapter.GetColumnDb(FlatDbColumns.Metadata), Is.SameAs(_inner.GetColumnDb(FlatDbColumns.Metadata)));
        Assert.That(_adapter.GetColumnDb(FlatDbColumns.StateNodes), Is.SameAs(_inner.GetColumnDb(FlatDbColumns.StateNodes)));
        Assert.That(_adapter.GetColumnDb(FlatDbColumns.StateTopNodes), Is.SameAs(_inner.GetColumnDb(FlatDbColumns.StateTopNodes)));
        Assert.That(_adapter.GetColumnDb(FlatDbColumns.StorageNodes), Is.SameAs(_inner.GetColumnDb(FlatDbColumns.StorageNodes)));
        Assert.That(_adapter.GetColumnDb(FlatDbColumns.FallbackNodes), Is.SameAs(_inner.GetColumnDb(FlatDbColumns.FallbackNodes)));
    }

    [Test]
    public void Account_and_storage_round_trip_through_the_adapter()
    {
        _adapter.GetColumnDb(FlatDbColumns.Account).Set(TestItem.KeccakA.Bytes, [1]);
        _adapter.GetColumnDb(FlatDbColumns.Storage).Set(TestItem.KeccakA.Bytes, [2]);

        Assert.That(_adapter.GetColumnDb(FlatDbColumns.Account).Get(TestItem.KeccakA.Bytes), Is.EqualTo(new byte[] { 1 }));
        Assert.That(_adapter.GetColumnDb(FlatDbColumns.Storage).Get(TestItem.KeccakA.Bytes), Is.EqualTo(new byte[] { 2 }));
    }

    [Test]
    public void Write_batch_commits_to_every_backend_it_touched()
    {
        using (IColumnsWriteBatch<FlatDbColumns> batch = _adapter.StartWriteBatch())
        {
            batch.GetColumnBatch(FlatDbColumns.Account).PutSpan(TestItem.KeccakA.Bytes, TestItem.KeccakA.Bytes);
            batch.GetColumnBatch(FlatDbColumns.Metadata).PutSpan(TestItem.KeccakB.Bytes, TestItem.KeccakB.Bytes);
        }

        Assert.That(_adapter.GetColumnDb(FlatDbColumns.Account).Get(TestItem.KeccakA.Bytes), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
        Assert.That(_adapter.GetColumnDb(FlatDbColumns.Metadata).Get(TestItem.KeccakB.Bytes), Is.EqualTo(TestItem.KeccakB.BytesToArray()));
    }

    [Test]
    public void ColumnKeys_reflects_the_inner_columns_db() =>
        Assert.That(_adapter.ColumnKeys, Is.EquivalentTo(_inner.ColumnKeys));

    [Test]
    public void CreateSnapshot_is_isolated_from_writes_made_after_it_was_taken_for_every_column()
    {
        _adapter.GetColumnDb(FlatDbColumns.Account).Set(TestItem.KeccakA.Bytes, [1]);
        _adapter.GetColumnDb(FlatDbColumns.Metadata).Set(TestItem.KeccakB.Bytes, [2]);

        using IColumnDbSnapshot<FlatDbColumns> snapshot = _adapter.CreateSnapshot();
        _adapter.GetColumnDb(FlatDbColumns.Account).Set(TestItem.KeccakA.Bytes, [99]);
        _adapter.GetColumnDb(FlatDbColumns.Metadata).Set(TestItem.KeccakB.Bytes, [99]);

        Assert.That(snapshot.GetColumn(FlatDbColumns.Account).Get(TestItem.KeccakA.Bytes), Is.EqualTo(new byte[] { 1 }));
        Assert.That(snapshot.GetColumn(FlatDbColumns.Metadata).Get(TestItem.KeccakB.Bytes), Is.EqualTo(new byte[] { 2 }));
    }

    [Test]
    public void CreateSnapshot_account_and_storage_columns_support_sorted_reads()
    {
        // RocksDbPersistence casts snapshot.GetColumn(Account/Storage) to ISortedKeyValueStore.
        _adapter.GetColumnDb(FlatDbColumns.Account).Set([1], [1]);
        _adapter.GetColumnDb(FlatDbColumns.Storage).Set([2], [2]);

        using IColumnDbSnapshot<FlatDbColumns> snapshot = _adapter.CreateSnapshot();

        Assert.That(snapshot.GetColumn(FlatDbColumns.Account), Is.InstanceOf<ISortedKeyValueStore>());
        Assert.That(snapshot.GetColumn(FlatDbColumns.Storage), Is.InstanceOf<ISortedKeyValueStore>());
        Assert.That(((ISortedKeyValueStore)snapshot.GetColumn(FlatDbColumns.Account)).FirstKey, Is.EqualTo(new byte[] { 1 }));
    }

    [Test]
    public void GatherMetric_combines_every_backend()
    {
        _adapter.GetColumnDb(FlatDbColumns.Account).Set(TestItem.KeccakA.Bytes, new byte[1024]);
        _adapter.GetColumnDb(FlatDbColumns.Account).Flush();

        IDbMeta.DbMetric metric = _adapter.GatherMetric();
        IDbMeta.DbMetric accountMetric = _adapter.GetColumnDb(FlatDbColumns.Account).GatherMetric();

        Assert.That(metric.Size, Is.GreaterThanOrEqualTo(accountMetric.Size));
    }
}
