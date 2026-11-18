// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
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
        public void Atomic_insertion_failure_preserves_publication_and_all_columns(
            [Values] bool failInsertion, [Values] bool neverPrune)
        {
            using SnapshotableMemColumnsDb<TestColumns> db = new(neverPrune);
            SnapshotableMemDb first = (SnapshotableMemDb)db.GetColumnDb(TestColumns.Column1);
            SnapshotableMemDb second = (SnapshotableMemDb)db.GetColumnDb(TestColumns.Column2);
            SnapshotableMemDb empty = (SnapshotableMemDb)db.GetColumnDb(TestColumns.Column3);
            first.Set([1], [2]);
            first.Set([2], [8]);
            second.Set([1], [3]);
            DatabaseState firstBefore = CaptureState(first);
            DatabaseState secondBefore = CaptureState(second);
            DatabaseState emptyBefore = CaptureState(empty);
            using IColumnDbSnapshot<TestColumns> before = db.CreateSnapshot();
            int priorInsertions = 0;
            int observedVersion = -1;
            long observedWrites = -1;
            InsertionFaultComparer comparer = new(secondBefore.Version + 1, failInsertion, () =>
            {
                priorInsertions = BackingStore(first).Count(entry => entry.Version == firstBefore.Version + 1);
                observedVersion = Version(first);
                observedWrites = first.WritesCount;
            });
            _storeField.SetValue(second, new SortedSet<(byte[] Key, int Version, byte[]? Value)>(BackingStore(second), comparer));
            using (IAtomicColumnsWriteBatch<TestColumns> batch = db.StartAtomicWriteBatch())
            {
                Stage(batch);
                if (failInsertion)
                {
                    Assert.That(Assert.Throws<IOException>(() => batch.Commit()), Is.SameAs(comparer.Failure));
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(comparer.Failures, Is.EqualTo(1));
                        Assert.That(priorInsertions, Is.EqualTo(3));
                        Assert.That(observedVersion, Is.EqualTo(firstBefore.Version));
                        Assert.That(observedWrites, Is.EqualTo(firstBefore.Writes));
                        AssertUnchanged(first, firstBefore);
                        AssertUnchanged(second, secondBefore);
                        AssertUnchanged(empty, emptyBefore);
                        Assert.That(first.Get([1]), Is.EqualTo(new byte[] { 2 }));
                        Assert.That(first.Get([2]), Is.EqualTo(new byte[] { 8 }));
                        Assert.That(second.Get([1]), Is.EqualTo(new byte[] { 3 }));
                        Assert.That(first.Get([9]), Is.Null);
                        Assert.That(second.Get([17]), Is.Null);
                        Assert.That(before.GetColumn(TestColumns.Column1).Get([1]), Is.EqualTo(new byte[] { 2 }));
                        Assert.That(before.GetColumn(TestColumns.Column1).Get([2]), Is.EqualTo(new byte[] { 8 }));
                        Assert.That(before.GetColumn(TestColumns.Column2).Get([1]), Is.EqualTo(new byte[] { 3 }));
                    }
                }
                else batch.Commit();
                Assert.Throws<ObjectDisposedException>(() => batch.Commit());
                Assert.Throws<ObjectDisposedException>(() => batch.GetColumnBatch(TestColumns.Column1));
            }
            if (failInsertion)
            {
                first.Set([33], [34]);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(first.Get([1]), Is.EqualTo(new byte[] { 2 }));
                    Assert.That(first.Get([2]), Is.EqualTo(new byte[] { 8 }));
                    Assert.That(first.Get([9]), Is.Null);
                }
                using IAtomicColumnsWriteBatch<TestColumns> retry = db.StartAtomicWriteBatch();
                Stage(retry);
                retry.Commit();
            }
            using IColumnDbSnapshot<TestColumns> after = db.CreateSnapshot();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(before.GetColumn(TestColumns.Column1).Get([1]), Is.EqualTo(new byte[] { 2 }));
                Assert.That(before.GetColumn(TestColumns.Column1).Get([2]), Is.EqualTo(new byte[] { 8 }));
                Assert.That(before.GetColumn(TestColumns.Column2).Get([1]), Is.EqualTo(new byte[] { 3 }));
                Assert.That(before.GetColumn(TestColumns.Column1).Get([9]), Is.Null);
                Assert.That(before.GetColumn(TestColumns.Column2).Get([17]), Is.Null);
                Assert.That(after.GetColumn(TestColumns.Column1).Get([1]), Is.EqualTo(new byte[] { 4 }));
                Assert.That(after.GetColumn(TestColumns.Column1).Get([2]), Is.Null);
                Assert.That(after.GetColumn(TestColumns.Column1).Get([9]), Is.EqualTo(new byte[] { 6 }));
                Assert.That(after.GetColumn(TestColumns.Column2).Get([1]), Is.EqualTo(new byte[] { 42 }));
                Assert.That(after.GetColumn(TestColumns.Column2).Get([17]), Is.EqualTo(new byte[] { 35 }));
                Assert.That(first.WritesCount, Is.EqualTo(firstBefore.Writes + 5 + (failInsertion ? 1 : 0)));
                Assert.That(second.WritesCount, Is.EqualTo(secondBefore.Writes + 2));
                Assert.That(Version(first), Is.EqualTo(firstBefore.Version + 1 + (failInsertion ? 1 : 0)));
                Assert.That(Version(second), Is.EqualTo(secondBefore.Version + 1));
                Assert.That(Version(empty), Is.EqualTo(emptyBefore.Version + 1));
                Assert.That(empty.WritesCount, Is.EqualTo(emptyBefore.Writes));
                if (failInsertion)
                {
                    Assert.That(after.GetColumn(TestColumns.Column1).Get([33]), Is.EqualTo(new byte[] { 34 }));
                    Assert.That(before.GetColumn(TestColumns.Column1).Get([33]), Is.Null);
                }
            }

            static void Stage(IAtomicColumnsWriteBatch<TestColumns> batch)
            {
                IWriteBatch first = batch.GetColumnBatch(TestColumns.Column1);
                first.Set([1], [99]);
                first.Set([1], [4]);
                first.Set([2], [7]);
                first.Set([2], null);
                first.Set([9], [6]);
                IWriteBatch second = batch.GetColumnBatch(TestColumns.Column2);
                second.Set([1], [42]);
                second.Set([17], [35]);
                batch.GetColumnBatch(TestColumns.Column3);
            }
        }

        [Test]
        public void Empty_atomic_columns_publish_only_explicitly_acquired_versions()
        {
            using SnapshotableMemColumnsDb<TestColumns> db = new();
            SnapshotableMemDb first = (SnapshotableMemDb)db.GetColumnDb(TestColumns.Column1);
            SnapshotableMemDb second = (SnapshotableMemDb)db.GetColumnDb(TestColumns.Column2);
            using (IAtomicColumnsWriteBatch<TestColumns> untouched = db.StartAtomicWriteBatch()) untouched.Commit();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Version(first), Is.Zero);
                Assert.That(Version(second), Is.Zero);
            }
            using (IAtomicColumnsWriteBatch<TestColumns> acquired = db.StartAtomicWriteBatch())
            {
                acquired.GetColumnBatch(TestColumns.Column1);
                acquired.Commit();
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Version(first), Is.EqualTo(1));
                Assert.That(Version(second), Is.Zero);
                Assert.That(first.WritesCount, Is.Zero);
                Assert.That(first.GetAllKeys(), Is.Empty);
            }
        }

        private static readonly FieldInfo _storeField = typeof(SnapshotableMemDb).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo _versionField = typeof(SnapshotableMemDb).GetField("_currentVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static SortedSet<(byte[] Key, int Version, byte[]? Value)> BackingStore(SnapshotableMemDb db) =>
            (SortedSet<(byte[] Key, int Version, byte[]? Value)>)_storeField.GetValue(db)!;

        private static int Version(SnapshotableMemDb db) => (int)_versionField.GetValue(db)!;

        private static DatabaseState CaptureState(SnapshotableMemDb db) => new(Version(db), db.WritesCount,
            BackingStore(db).Select(entry => (Convert.ToHexString(entry.Key), entry.Version, entry.Value is null ? null : Convert.ToHexString(entry.Value))).ToArray());

        private static void AssertUnchanged(SnapshotableMemDb db, DatabaseState expected)
        {
            DatabaseState actual = CaptureState(db);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual.Version, Is.EqualTo(expected.Version));
                Assert.That(actual.Writes, Is.EqualTo(expected.Writes));
                Assert.That(actual.Entries, Is.EqualTo(expected.Entries));
            }
        }

        private sealed record DatabaseState(int Version, long Writes, (string Key, int Version, string? Value)[] Entries);

        private sealed class InsertionFaultComparer(int failVersion, bool armed, Action beforeFailure)
            : IComparer<(byte[] Key, int Version, byte[]? Value)>
        {
            private bool _armed = armed;
            internal int Failures;
            internal IOException Failure { get; } = new("Injected atomic insertion boundary failure.");

            public int Compare((byte[] Key, int Version, byte[]? Value) x, (byte[] Key, int Version, byte[]? Value) y)
            {
                if (_armed && (IsNewWrite(x) || IsNewWrite(y)))
                {
                    _armed = false;
                    Failures++;
                    beforeFailure();
                    throw Failure;
                }
                int keys = x.Key.AsSpan().SequenceCompareTo(y.Key);
                return keys == 0 ? x.Version.CompareTo(y.Version) : keys;
            }

            private bool IsNewWrite((byte[] Key, int Version, byte[]? Value) entry) =>
                entry.Version == failVersion && entry.Value is [42];
        }

        [Test]
        public void Atomic_preparation_failure_does_not_publish_an_earlier_column()
        {
            using SnapshotableMemColumnsDb<TestColumns> db = new();
            SnapshotableMemDb first = (SnapshotableMemDb)db.GetColumnDb(TestColumns.Column1);
            SnapshotableMemDb second = (SnapshotableMemDb)db.GetColumnDb(TestColumns.Column2);
            first.Set([1], [2]);
            second.Set([1], [3]);
            System.Reflection.FieldInfo version = typeof(SnapshotableMemDb).GetField("_currentVersion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            version.SetValue(second, int.MaxValue);
            using (IAtomicColumnsWriteBatch<TestColumns> batch = db.StartAtomicWriteBatch())
            {
                batch.GetColumnBatch(TestColumns.Column1).Set([1], [4]);
                batch.GetColumnBatch(TestColumns.Column2).Set([1], [5]);
                Assert.Throws<OverflowException>(() => batch.Commit());
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(first.Get([1]), Is.EqualTo(new byte[] { 2 }));
                Assert.That(second.Get([1]), Is.EqualTo(new byte[] { 3 }));
            }
            version.SetValue(second, 1);
            using (IAtomicColumnsWriteBatch<TestColumns> retry = db.StartAtomicWriteBatch())
            {
                retry.GetColumnBatch(TestColumns.Column1).Set([1], [4]);
                retry.GetColumnBatch(TestColumns.Column2).Set([1], [5]);
                retry.Commit();
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(first.Get([1]), Is.EqualTo(new byte[] { 4 }));
                Assert.That(second.Get([1]), Is.EqualTo(new byte[] { 5 }));
            }
        }

        [Test]
        public void Atomic_columns_preserve_snapshots_and_abort_without_publication([Values] bool commit)
        {
            using SnapshotableMemColumnsDb<TestColumns> db = new();
            db.GetColumnDb(TestColumns.Column1).Set([1], [2]);
            db.GetColumnDb(TestColumns.Column2).Set([1], [3]);
            using IColumnDbSnapshot<TestColumns> before = db.CreateSnapshot();
            using (IAtomicColumnsWriteBatch<TestColumns> batch = db.StartAtomicWriteBatch())
            {
                batch.GetColumnBatch(TestColumns.Column1).Set([1], [4], WriteFlags.DisableWAL);
                batch.GetColumnBatch(TestColumns.Column2).Set([1], [5], WriteFlags.DisableWAL);
                for (int i = 0; i < 300; i++)
                {
                    batch.GetColumnBatch(TestColumns.Column1).Set(BitConverter.GetBytes(i), [6], WriteFlags.DisableWAL);
                    batch.GetColumnBatch(TestColumns.Column2).Set(BitConverter.GetBytes(i), [7], WriteFlags.DisableWAL);
                }
                if (commit) batch.Commit();
            }
            using IColumnDbSnapshot<TestColumns> after = db.CreateSnapshot();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(before.GetColumn(TestColumns.Column1).Get([1]), Is.EqualTo(new byte[] { 2 }));
                Assert.That(before.GetColumn(TestColumns.Column2).Get([1]), Is.EqualTo(new byte[] { 3 }));
                Assert.That(after.GetColumn(TestColumns.Column1).Get([1]), Is.EqualTo(new byte[] { commit ? (byte)4 : (byte)2 }));
                Assert.That(after.GetColumn(TestColumns.Column2).Get([1]), Is.EqualTo(new byte[] { commit ? (byte)5 : (byte)3 }));
                for (int i = 0; i < 300; i++)
                {
                    Assert.That(after.GetColumn(TestColumns.Column1).Get(BitConverter.GetBytes(i)), commit ? Is.EqualTo(new byte[] { 6 }) : Is.Null);
                    Assert.That(after.GetColumn(TestColumns.Column2).Get(BitConverter.GetBytes(i)), commit ? Is.EqualTo(new byte[] { 7 }) : Is.Null);
                }
            }
        }

        [Test]
        public void Can_create_and_get_columns()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();

            IDb column1 = columnsDb.GetColumnDb(TestColumns.Column1);
            IDb column2 = columnsDb.GetColumnDb(TestColumns.Column2);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(column1, Is.Not.Null);
                Assert.That(column2, Is.Not.Null);
            }
            Assert.That(column1, Is.Not.SameAs(column2));
        }

        [Test]
        public void Can_write_and_read_from_columns()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();

            IDb column1 = columnsDb.GetColumnDb(TestColumns.Column1);
            IDb column2 = columnsDb.GetColumnDb(TestColumns.Column2);

            column1.Set(TestItem.KeccakA, _sampleValue);
            column2.Set(TestItem.KeccakA, _sampleValue2);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(column1.Get(TestItem.KeccakA), Is.EqualTo(_sampleValue));
                Assert.That(column2.Get(TestItem.KeccakA), Is.EqualTo(_sampleValue2));
            }
        }

        [Test]
        public void Can_create_snapshot()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();

            IDb column1 = columnsDb.GetColumnDb(TestColumns.Column1);
            column1.Set(TestItem.KeccakA, _sampleValue);

            IColumnDbSnapshot<TestColumns> snapshot = columnsDb.CreateSnapshot();
            Assert.That(snapshot, Is.Not.Null);

            IReadOnlyKeyValueStore snapshotColumn = snapshot.GetColumn(TestColumns.Column1);
            Assert.That(snapshotColumn.Get(TestItem.KeccakA), Is.EqualTo(_sampleValue));

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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshotColumn.Get(TestItem.KeccakA), Is.EqualTo(_sampleValue));
                Assert.That(snapshotColumn.Get(TestItem.KeccakB), Is.Null);

                // Main db should see new values
                Assert.That(column1.Get(TestItem.KeccakA), Is.EqualTo(_sampleValue2));
                Assert.That(column1.Get(TestItem.KeccakB), Is.EqualTo(_sampleValue2));
            }

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

            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshotColumn1.Get(TestItem.KeccakA), Is.EqualTo(new byte[] { 1 }));
                Assert.That(snapshotColumn2.Get(TestItem.KeccakA), Is.EqualTo(new byte[] { 2 }));
            }

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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshot1.GetColumn(TestColumns.Column1).Get(TestItem.KeccakA), Is.EqualTo(new byte[] { 1 }));
                Assert.That(snapshot2.GetColumn(TestColumns.Column1).Get(TestItem.KeccakA), Is.EqualTo(new byte[] { 2 }));
            }

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
            Assert.That(column1.Get(TestItem.KeccakA), Is.EqualTo(_sampleValue));
        }

        [Test]
        public void Snapshot_never_observes_a_partially_committed_write_batch()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();
            const int batchCount = 5000;
            using ManualResetEventSlim readerStarted = new();

            Task writer = Task.Run(() =>
            {
                readerStarted.Wait();
                for (int i = 1; i <= batchCount; i++)
                {
                    byte[] value = BitConverter.GetBytes(i);
                    using IColumnsWriteBatch<TestColumns> batch = columnsDb.StartWriteBatch();
                    batch.GetColumnBatch(TestColumns.Column1).Set(TestItem.KeccakA, value);
                    batch.GetColumnBatch(TestColumns.Column1).Set(TestItem.KeccakB, value);
                    batch.GetColumnBatch(TestColumns.Column2).Set(TestItem.KeccakA, value);
                }
            });

            int snapshotCount = 0;
            int tornSnapshots = 0;
            readerStarted.Set();
            while (!writer.IsCompleted)
            {
                snapshotCount++;
                using IColumnDbSnapshot<TestColumns> snapshot = columnsDb.CreateSnapshot();
                byte[]? column1A = snapshot.GetColumn(TestColumns.Column1).Get(TestItem.KeccakA);
                byte[]? column1B = snapshot.GetColumn(TestColumns.Column1).Get(TestItem.KeccakB);
                byte[]? column2A = snapshot.GetColumn(TestColumns.Column2).Get(TestItem.KeccakA);
                if (!Bytes.AreEqual(column1A, column1B) || !Bytes.AreEqual(column1A, column2A)) tornSnapshots++;
            }

            writer.GetAwaiter().GetResult();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshotCount, Is.GreaterThan(0), "no snapshot overlapped the writer");
                Assert.That(tornSnapshots, Is.Zero);
            }
        }

        [Test]
        public void ColumnKeys_returns_all_columns()
        {
            SnapshotableMemColumnsDb<TestColumns> columnsDb = new();

            columnsDb.GetColumnDb(TestColumns.Column1);
            columnsDb.GetColumnDb(TestColumns.Column2);

            Assert.That(columnsDb.ColumnKeys, Does.Contain(TestColumns.Column1));
            Assert.That(columnsDb.ColumnKeys, Does.Contain(TestColumns.Column2));
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
            Assert.That(snapshotColumn, Is.AssignableTo<ISortedKeyValueStore>());

            ISortedKeyValueStore sortedColumn = (ISortedKeyValueStore)snapshotColumn;
            byte[]? firstKey = sortedColumn.FirstKey;
            byte[]? lastKey = sortedColumn.LastKey;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstKey, Is.Not.Null);
                Assert.That(lastKey, Is.Not.Null);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstKey, Is.EqualTo(keyA));  // 0x01
                Assert.That(lastKey, Is.EqualTo(keyC));   // 0x03
            }

            snapshot.Dispose();
        }
    }
}
