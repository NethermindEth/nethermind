// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class SnapshotableMemColumnsDbTests
    {
        private enum TestColumns
        {
            Column1,
            Column2,
            Column3
        }

        private readonly byte[] _sampleValue = { 1, 2, 3 };
        private readonly byte[] _sampleValue2 = { 4, 5, 6 };

        [Test]
        public void Can_create_and_get_columns()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();

            IDb column1 = columnsDb.GetColumnDb(TestColumns.Column1);
            IDb column2 = columnsDb.GetColumnDb(TestColumns.Column2);

            column1.Should().NotBeNull();
            column2.Should().NotBeNull();
            column1.Should().NotBeSameAs(column2);
        }

        [Test]
        public void Can_write_and_read_from_columns()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();

            IDb column1 = columnsDb.GetColumnDb(TestColumns.Column1);
            IDb column2 = columnsDb.GetColumnDb(TestColumns.Column2);

            column1.Set(TestItem.KeccakA, _sampleValue);
            column2.Set(TestItem.KeccakA, _sampleValue2);

            column1.Get(TestItem.KeccakA).Should().BeEquivalentTo(_sampleValue);
            column2.Get(TestItem.KeccakA).Should().BeEquivalentTo(_sampleValue2);
        }

        [Test]
        public void Can_create_snapshot()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();

            IDb column1 = columnsDb.GetColumnDb(TestColumns.Column1);
            column1.Set(TestItem.KeccakA, _sampleValue);

            IColumnDbSnapshot<TestColumns> snapshot = columnsDb.CreateSnapshot();
            snapshot.Should().NotBeNull();

            IReadOnlyKeyValueStore snapshotColumn = snapshot.GetColumn(TestColumns.Column1);
            snapshotColumn.Get(TestItem.KeccakA).Should().BeEquivalentTo(_sampleValue);

            snapshot.Dispose();
        }

        [Test]
        public void Snapshot_is_isolated_from_subsequent_writes()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();

            IDb column1 = columnsDb.GetColumnDb(TestColumns.Column1);
            column1.Set(TestItem.KeccakA, _sampleValue);

            IColumnDbSnapshot<TestColumns> snapshot = columnsDb.CreateSnapshot();

            // Modify after snapshot
            column1.Set(TestItem.KeccakA, _sampleValue2);
            column1.Set(TestItem.KeccakB, _sampleValue2);

            // Snapshot should see old values
            IReadOnlyKeyValueStore snapshotColumn = snapshot.GetColumn(TestColumns.Column1);
            snapshotColumn.Get(TestItem.KeccakA).Should().BeEquivalentTo(_sampleValue);
            snapshotColumn.Get(TestItem.KeccakB).Should().BeNull();

            // Main db should see new values
            column1.Get(TestItem.KeccakA).Should().BeEquivalentTo(_sampleValue2);
            column1.Get(TestItem.KeccakB).Should().BeEquivalentTo(_sampleValue2);

            snapshot.Dispose();
        }

        [Test]
        public void Snapshot_captures_all_columns_atomically()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();

            IDb column1 = columnsDb.GetColumnDb(TestColumns.Column1);
            IDb column2 = columnsDb.GetColumnDb(TestColumns.Column2);

            column1.Set(TestItem.KeccakA, new byte[] { 1 });
            column2.Set(TestItem.KeccakA, new byte[] { 2 });

            IColumnDbSnapshot<TestColumns> snapshot = columnsDb.CreateSnapshot();

            // Modify both columns
            column1.Set(TestItem.KeccakA, new byte[] { 10 });
            column2.Set(TestItem.KeccakA, new byte[] { 20 });

            // Snapshot should see old values in both columns
            IReadOnlyKeyValueStore snapshotColumn1 = snapshot.GetColumn(TestColumns.Column1);
            IReadOnlyKeyValueStore snapshotColumn2 = snapshot.GetColumn(TestColumns.Column2);

            snapshotColumn1.Get(TestItem.KeccakA).Should().BeEquivalentTo(new byte[] { 1 });
            snapshotColumn2.Get(TestItem.KeccakA).Should().BeEquivalentTo(new byte[] { 2 });

            snapshot.Dispose();
        }

        [Test]
        public void Multiple_snapshots_are_independent()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();

            IDb column1 = columnsDb.GetColumnDb(TestColumns.Column1);

            column1.Set(TestItem.KeccakA, new byte[] { 1 });
            IColumnDbSnapshot<TestColumns> snapshot1 = columnsDb.CreateSnapshot();

            column1.Set(TestItem.KeccakA, new byte[] { 2 });
            IColumnDbSnapshot<TestColumns> snapshot2 = columnsDb.CreateSnapshot();

            column1.Set(TestItem.KeccakA, new byte[] { 3 });

            // Each snapshot sees its version
            snapshot1.GetColumn(TestColumns.Column1).Get(TestItem.KeccakA).Should().BeEquivalentTo(new byte[] { 1 });
            snapshot2.GetColumn(TestColumns.Column1).Get(TestItem.KeccakA).Should().BeEquivalentTo(new byte[] { 2 });

            snapshot1.Dispose();
            snapshot2.Dispose();
        }

        [Test]
        public void Can_use_write_batch()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();

            using (IColumnsWriteBatch<TestColumns> batch = columnsDb.StartWriteBatch())
            {
                batch.GetColumnBatch(TestColumns.Column1).Set(TestItem.KeccakA, _sampleValue);
            }

            IDb column1 = columnsDb.GetColumnDb(TestColumns.Column1);
            column1.Get(TestItem.KeccakA).Should().BeEquivalentTo(_sampleValue);
        }

        [Test]
        public void Flush_does_not_cause_trouble()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();
            columnsDb.Flush();
        }

        [Test]
        public void Dispose_does_not_cause_trouble()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();
            columnsDb.Dispose();
        }

        [Test]
        public void ColumnKeys_returns_all_columns()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();

            columnsDb.GetColumnDb(TestColumns.Column1);
            columnsDb.GetColumnDb(TestColumns.Column2);

            columnsDb.ColumnKeys.Should().Contain(TestColumns.Column1);
            columnsDb.ColumnKeys.Should().Contain(TestColumns.Column2);
        }

        [Test]
        public void Snapshot_column_supports_ISortedKeyValueStore()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();
            byte[] keyA = new byte[] { 0x01 };
            byte[] keyB = new byte[] { 0x02 };
            byte[] keyC = new byte[] { 0x03 };

            IDb column1 = columnsDb.GetColumnDb(TestColumns.Column1);
            column1.Set(keyA, new byte[] { 1 });
            column1.Set(keyB, new byte[] { 2 });
            column1.Set(keyC, new byte[] { 3 });

            IColumnDbSnapshot<TestColumns> snapshot = columnsDb.CreateSnapshot();
            IReadOnlyKeyValueStore snapshotColumn = snapshot.GetColumn(TestColumns.Column1);

            // Check if snapshot column is ISortedKeyValueStore
            snapshotColumn.Should().BeAssignableTo<ISortedKeyValueStore>();

            ISortedKeyValueStore sortedColumn = (ISortedKeyValueStore)snapshotColumn;
            byte[]? firstKey = sortedColumn.FirstKey;
            byte[]? lastKey = sortedColumn.LastKey;

            firstKey.Should().NotBeNull();
            lastKey.Should().NotBeNull();

            firstKey.Should().BeEquivalentTo(keyA);  // 0x01
            lastKey.Should().BeEquivalentTo(keyC);   // 0x03

            snapshot.Dispose();
        }
    }
}
