// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Db.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class SnapshotableMemDbTests
    {
        private readonly byte[] _sampleValue = { 1, 2, 3 };
        private readonly byte[] _sampleValue2 = { 4, 5, 6 };

        [Test]
        public void Simple_set_get_is_fine()
        {
            SnapshotableMemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            byte[] retrievedBytes = memDb.Get(TestItem.KeccakA);
            Assert.That(retrievedBytes, Is.EqualTo(_sampleValue));
        }

        [Test]
        public void Can_create_with_name()
        {
            SnapshotableMemDb memDb = new("test_db");
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Get(TestItem.KeccakA);
            Assert.That(memDb.Name, Is.EqualTo("test_db"));
        }

        [Test]
        public void Can_create_snapshot()
        {
            SnapshotableMemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);

            IKeyValueStoreSnapshot snapshot = memDb.CreateSnapshot();
            Assert.That(snapshot, Is.Not.Null);

            byte[] value = snapshot.Get(TestItem.KeccakA);
            Assert.That(value, Is.EqualTo(_sampleValue));

            snapshot.Dispose();
        }

        [Test]
        public void Snapshot_is_isolated_from_subsequent_writes()
        {
            SnapshotableMemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);

            IKeyValueStoreSnapshot snapshot = memDb.CreateSnapshot();

            // Modify after snapshot
            memDb.Set(TestItem.KeccakA, _sampleValue2);
            memDb.Set(TestItem.KeccakB, _sampleValue2);

            // Snapshot should see old values
            byte[] valueA = snapshot.Get(TestItem.KeccakA);
            Assert.That(valueA, Is.EqualTo(_sampleValue));

            byte[] valueB = snapshot.Get(TestItem.KeccakB);
            Assert.That(valueB, Is.Null);

            // Main db should see new values
            Assert.That(memDb.Get(TestItem.KeccakA), Is.EqualTo(_sampleValue2));
            Assert.That(memDb.Get(TestItem.KeccakB), Is.EqualTo(_sampleValue2));

            snapshot.Dispose();
        }

        [Test]
        public void Multiple_snapshots_see_correct_versions()
        {
            SnapshotableMemDb memDb = new();

            // Version 1
            memDb.Set(TestItem.KeccakA, new byte[] { 1 });
            IKeyValueStoreSnapshot snapshot1 = memDb.CreateSnapshot();

            // Version 2
            memDb.Set(TestItem.KeccakA, new byte[] { 2 });
            IKeyValueStoreSnapshot snapshot2 = memDb.CreateSnapshot();

            // Version 3
            memDb.Set(TestItem.KeccakA, new byte[] { 3 });
            IKeyValueStoreSnapshot snapshot3 = memDb.CreateSnapshot();

            // Check each snapshot sees its version
            byte[]? value1 = snapshot1.Get(TestItem.KeccakA);
            byte[]? value2 = snapshot2.Get(TestItem.KeccakA);
            byte[]? value3 = snapshot3.Get(TestItem.KeccakA);

            Assert.That(value1, Is.Not.Null);
            Assert.That(value2, Is.Not.Null);
            Assert.That(value3, Is.Not.Null);

            Assert.That(value1, Is.EqualTo(new byte[] { 1 }));
            Assert.That(value2, Is.EqualTo(new byte[] { 2 }));
            Assert.That(value3, Is.EqualTo(new byte[] { 3 }));

            snapshot1.Dispose();
            snapshot2.Dispose();
            snapshot3.Dispose();
        }

        [Test]
        public void Disposing_all_snapshots_clears_old_versions()
        {
            SnapshotableMemDb memDb = new();
            memDb.Set(TestItem.KeccakA, new byte[] { 1 });

            IKeyValueStoreSnapshot snapshot1 = memDb.CreateSnapshot();
            memDb.Set(TestItem.KeccakA, new byte[] { 2 });

            IKeyValueStoreSnapshot snapshot2 = memDb.CreateSnapshot();
            memDb.Set(TestItem.KeccakA, new byte[] { 3 });

            // Dispose all snapshots
            snapshot1.Dispose();
            snapshot2.Dispose();

            // After disposal, old versions should be pruned
            // Main db should still work
            Assert.That(memDb.Get(TestItem.KeccakA), Is.EqualTo(new byte[] { 3 }));
            memDb.Set(TestItem.KeccakA, new byte[] { 4 });
            Assert.That(memDb.Get(TestItem.KeccakA), Is.EqualTo(new byte[] { 4 }));
        }

        [Test]
        public void Can_remove_key()
        {
            SnapshotableMemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Remove(TestItem.KeccakA.Bytes);
            Assert.That(memDb.KeyExists(TestItem.KeccakA), Is.False);
        }

        [Test]
        public void Snapshot_sees_removed_key_as_existing_if_removed_after_snapshot()
        {
            SnapshotableMemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);

            IKeyValueStoreSnapshot snapshot = memDb.CreateSnapshot();

            memDb.Remove(TestItem.KeccakA.Bytes);

            // Main db should not see key
            Assert.That(memDb.KeyExists(TestItem.KeccakA), Is.False);

            // Snapshot should still see key
            Assert.That(snapshot.KeyExists(TestItem.KeccakA), Is.True);
            Assert.That(snapshot.Get(TestItem.KeccakA), Is.EqualTo(_sampleValue));

            snapshot.Dispose();
        }

        [Test]
        public void Can_get_keys()
        {
            SnapshotableMemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            Assert.That(memDb.Keys, Has.Count.EqualTo(2));
        }

        [Test]
        public void Can_get_all()
        {
            SnapshotableMemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            Assert.That(System.Linq.Enumerable.Count(memDb.GetAllValues()), Is.EqualTo(2));
        }

        [Test]
        public void Can_get_values()
        {
            SnapshotableMemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            Assert.That(memDb.Values, Has.Count.EqualTo(2));
        }

        [Test]
        public void Dispose_does_not_cause_trouble()
        {
            SnapshotableMemDb memDb = new();
            memDb.Dispose();
        }

        [Test]
        public void Flush_does_not_cause_trouble()
        {
            SnapshotableMemDb memDb = new();
            memDb.Flush();
        }

        [Test]
        public void Can_clear()
        {
            SnapshotableMemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            memDb.Clear();
            Assert.That(memDb.Keys, Has.Count.EqualTo(0));
        }

        [Test]
        public void FirstKey_returns_sorted_first_key()
        {
            SnapshotableMemDb memDb = new();
            byte[] keyA = new byte[] { 0x01 };
            byte[] keyB = new byte[] { 0x02 };
            byte[] keyC = new byte[] { 0x03 };

            memDb.Set(keyC, _sampleValue);  // Insert out of order
            memDb.Set(keyA, _sampleValue);
            memDb.Set(keyB, _sampleValue);

            byte[]? firstKey = memDb.FirstKey;
            Assert.That(firstKey, Is.Not.Null);
            Assert.That(firstKey, Is.EqualTo(keyA));  // 0x01 is smallest
        }

        [Test]
        public void LastKey_returns_sorted_last_key()
        {
            SnapshotableMemDb memDb = new();
            byte[] keyA = new byte[] { 0x01 };
            byte[] keyB = new byte[] { 0x02 };
            byte[] keyC = new byte[] { 0x03 };

            memDb.Set(keyA, _sampleValue);  // Insert out of order
            memDb.Set(keyC, _sampleValue);
            memDb.Set(keyB, _sampleValue);

            byte[]? lastKey = memDb.LastKey;
            Assert.That(lastKey, Is.Not.Null);
            Assert.That(lastKey, Is.EqualTo(keyC));  // 0x03 is largest
        }

        [Test]
        public void GetViewBetween_returns_keys_in_range()
        {
            SnapshotableMemDb memDb = new();
            byte[] keyA = new byte[] { 0x01 };
            byte[] keyB = new byte[] { 0x02 };
            byte[] keyC = new byte[] { 0x03 };
            byte[] keyD = new byte[] { 0x04 };
            byte[] keyE = new byte[] { 0x05 };

            memDb.Set(keyA, new byte[] { 1 });
            memDb.Set(keyB, new byte[] { 2 });
            memDb.Set(keyC, new byte[] { 3 });
            memDb.Set(keyD, new byte[] { 4 });
            memDb.Set(keyE, new byte[] { 5 });

            // Get keys between B (inclusive) and E (exclusive)
            ISortedView view = memDb.GetViewBetween(keyB, keyE);

            List<byte[]> keys = [];
            List<byte[]> values = [];
            while (view.MoveNext())
            {
                keys.Add(view.CurrentKey.ToArray());
                values.Add(view.CurrentValue.ToArray());
            }

            Assert.That(keys, Has.Count.EqualTo(3));
            Assert.That(keys[0], Is.EqualTo(keyB));
            Assert.That(keys[1], Is.EqualTo(keyC));
            Assert.That(keys[2], Is.EqualTo(keyD));

            Assert.That(values[0], Is.EqualTo(new byte[] { 2 }));
            Assert.That(values[1], Is.EqualTo(new byte[] { 3 }));
            Assert.That(values[2], Is.EqualTo(new byte[] { 4 }));

            view.Dispose();
        }

        [Test]
        public void Snapshot_GetViewBetween_sees_correct_version()
        {
            SnapshotableMemDb memDb = new();
            byte[] keyA = new byte[] { 0x01 };
            byte[] keyB = new byte[] { 0x02 };
            byte[] keyC = new byte[] { 0x03 };
            byte[] keyD = new byte[] { 0x04 };
            byte[] keyE = new byte[] { 0x05 };

            memDb.Set(keyA, new byte[] { 1 });
            memDb.Set(keyB, new byte[] { 2 });
            memDb.Set(keyC, new byte[] { 3 });

            IKeyValueStoreSnapshot snapshot = memDb.CreateSnapshot();

            // Modify after snapshot
            memDb.Set(keyB, new byte[] { 99 });
            memDb.Set(keyD, new byte[] { 4 });

            // Snapshot view should see old version
            ISortedKeyValueStore sortedSnapshot = (ISortedKeyValueStore)snapshot;
            ISortedView view = sortedSnapshot.GetViewBetween(keyA, keyE);

            List<byte[]> values = [];
            while (view.MoveNext())
            {
                values.Add(view.CurrentValue.ToArray());
            }

            Assert.That(values, Has.Count.EqualTo(3));
            Assert.That(values[0], Is.EqualTo(new byte[] { 1 }));
            Assert.That(values[1], Is.EqualTo(new byte[] { 2 })); // Old version
            Assert.That(values[2], Is.EqualTo(new byte[] { 3 }));

            view.Dispose();
            snapshot.Dispose();
        }

        [Test]
        public void Can_use_batches()
        {
            SnapshotableMemDb memDb = new();
            using (IWriteBatch batch = memDb.StartWriteBatch())
            {
                batch.Set(TestItem.KeccakA, _sampleValue);
            }

            byte[] retrieved = memDb.Get(TestItem.KeccakA);
            Assert.That(retrieved, Is.EqualTo(_sampleValue));
        }

        [Test]
        public void Can_get_all_ordered()
        {
            SnapshotableMemDb memDb = new();

            memDb.Set(TestItem.KeccakE, _sampleValue);
            memDb.Set(TestItem.KeccakC, _sampleValue);
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            memDb.Set(TestItem.KeccakD, _sampleValue);

            IEnumerable<KeyValuePair<byte[], byte[]?>> orderedItems = memDb.GetAll(true);

            Assert.That(System.Linq.Enumerable.Count(orderedItems), Is.EqualTo(5));

            byte[][] keys = orderedItems.Select(kvp => kvp.Key).ToArray();
            for (int i = 0; i < keys.Length - 1; i++)
            {
                Assert.That(Bytes.BytesComparer.Compare(keys[i], keys[i + 1]), Is.LessThan(0), $"Keys should be in ascending order at position {i}");
            }
        }

        [Test]
        public void Snapshot_FirstKey_and_LastKey_work()
        {
            SnapshotableMemDb memDb = new();
            byte[] keyA = new byte[] { 0x01 };
            byte[] keyB = new byte[] { 0x02 };
            byte[] keyC = new byte[] { 0x03 };

            memDb.Set(keyA, _sampleValue);
            memDb.Set(keyB, _sampleValue);

            IKeyValueStoreSnapshot snapshot = memDb.CreateSnapshot();

            memDb.Set(keyC, _sampleValue);  // Add after snapshot

            ISortedKeyValueStore sortedSnapshot = (ISortedKeyValueStore)snapshot;
            byte[]? firstKey = sortedSnapshot.FirstKey;
            byte[]? lastKey = sortedSnapshot.LastKey;

            Assert.That(firstKey, Is.Not.Null);
            Assert.That(lastKey, Is.Not.Null);

            Assert.That(firstKey, Is.EqualTo(keyA));  // 0x01
            Assert.That(lastKey, Is.EqualTo(keyB));   // 0x02 (not keyC which was added after)

            snapshot.Dispose();
        }

        [Test]
        public void Snapshot_survives_pruning_when_newer_snapshot_disposed()
        {
            // Use default (pruning enabled) to verify the fix
            SnapshotableMemDb memDb = new();

            // Write key A at version 1
            memDb.Set(TestItem.KeccakA, new byte[] { 1 });

            // Write key B at version 2
            memDb.Set(TestItem.KeccakB, new byte[] { 2 });

            // Create snapshot1 at version 2 (sees both keys)
            IKeyValueStoreSnapshot snapshot1 = memDb.CreateSnapshot();

            // Update key A at version 3
            memDb.Set(TestItem.KeccakA, new byte[] { 3 });

            // Create snapshot2 at version 3
            IKeyValueStoreSnapshot snapshot2 = memDb.CreateSnapshot();

            // Dispose snapshot2 - triggers PruneVersionsOlderThan(2)
            snapshot2.Dispose();

            // snapshot1 should still see the original value for key A
            byte[]? valueA = snapshot1.Get(TestItem.KeccakA);
            Assert.That(valueA, Is.Not.Null, "snapshot1 at version 2 should still see key A written at version 1");
            Assert.That(valueA, Is.EqualTo(new byte[] { 1 }));

            // Key B should still work
            byte[]? valueB = snapshot1.Get(TestItem.KeccakB);
            Assert.That(valueB, Is.EqualTo(new byte[] { 2 }));

            snapshot1.Dispose();
        }

        [Test]
        public void NeverPrune_option_disables_pruning()
        {
            SnapshotableMemDb memDb = new(neverPrune: true);

            memDb.Set(TestItem.KeccakA, new byte[] { 1 });
            IKeyValueStoreSnapshot snapshot1 = memDb.CreateSnapshot();

            memDb.Set(TestItem.KeccakA, new byte[] { 2 });
            IKeyValueStoreSnapshot snapshot2 = memDb.CreateSnapshot();

            // Dispose in any order - no pruning should occur
            snapshot2.Dispose();

            // snapshot1 should still work
            byte[]? value = snapshot1.Get(TestItem.KeccakA);
            Assert.That(value, Is.EqualTo(new byte[] { 1 }));

            snapshot1.Dispose();
        }
    }
}
