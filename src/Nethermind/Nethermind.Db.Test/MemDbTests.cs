// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class MemDbTests
    {
        [Test]
        public void Simple_set_get_is_fine()
        {
            IDb memDb = new MemDb();
            byte[] bytes = new byte[] { 1, 2, 3 };
            memDb.Set(TestItem.KeccakA, bytes);
            byte[] retrievedBytes = memDb.Get(TestItem.KeccakA);
            Assert.That(retrievedBytes, Is.EqualTo(bytes));
        }

        private readonly byte[] _sampleValue = { 1, 2, 3 };

        [Test]
        public void Can_create_with_delays()
        {
            MemDb memDb = new(10, 10);
            memDb.Set(TestItem.KeccakA, new byte[] { 1, 2, 3 });
            memDb.Get(TestItem.KeccakA);
            _ = memDb[new[] { TestItem.KeccakA.BytesToArray() }];
        }

        [Test]
        public void Can_create_with_name()
        {
            MemDb memDb = new("desc");
            memDb.Set(TestItem.KeccakA, new byte[] { 1, 2, 3 });
            memDb.Get(TestItem.KeccakA);
            Assert.That(memDb.Name, Is.EqualTo("desc"));
        }

        [Test]
        public void Can_create_without_arguments()
        {
            MemDb memDb = new();
            memDb.Set(TestItem.KeccakA, new byte[] { 1, 2, 3 });
            memDb.Get(TestItem.KeccakA);
        }

        [Test]
        public void Can_use_batches_without_issues()
        {
            MemDb memDb = new();
            using (memDb.StartWriteBatch())
            {
                memDb.Set(TestItem.KeccakA, _sampleValue);
            }

            byte[] retrieved = memDb.Get(TestItem.KeccakA);
            Assert.That(retrieved, Is.EqualTo(_sampleValue));
        }

        [Test]
        public void Can_delete()
        {
            MemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            memDb.Clear();
            Assert.That(memDb.Keys, Has.Count.EqualTo(0));
        }

        [Test]
        public void Can_check_if_key_exists()
        {
            MemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            Assert.That(memDb.KeyExists(TestItem.KeccakA), Is.True);
            Assert.That(memDb.KeyExists(TestItem.KeccakB), Is.False);
        }

        [Test]
        public void Can_remove_key()
        {
            MemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Remove(TestItem.KeccakA.Bytes);
            Assert.That(memDb.KeyExists(TestItem.KeccakA), Is.False);
        }

        [Test]
        public void Can_get_keys()
        {
            MemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            Assert.That(memDb.Keys, Has.Count.EqualTo(2));
        }

        [Test]
        public void Can_get_some_keys()
        {
            MemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            KeyValuePair<byte[], byte[]>[] result = memDb[new[] { TestItem.KeccakB.BytesToArray(), TestItem.KeccakB.BytesToArray(), TestItem.KeccakC.BytesToArray() }];
            Assert.That(result, Has.Length.EqualTo(3));
            Assert.That(result[0].Value, Is.Not.Null);
            Assert.That(result[1].Value, Is.Not.Null);
            Assert.That(result[2].Value, Is.Null);
        }

        [Test]
        public void Can_get_all()
        {
            MemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            Assert.That(System.Linq.Enumerable.Count(memDb.GetAllValues()), Is.EqualTo(2));
        }

        [Test]
        public void Can_get_values()
        {
            MemDb memDb = new();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            Assert.That(memDb.Values, Has.Count.EqualTo(2));
        }

        [Test]
        public void Dispose_does_not_cause_trouble()
        {
            MemDb memDb = new();
            memDb.Dispose();
        }

        [Test]
        public void Flush_does_not_cause_trouble()
        {
            MemDb memDb = new();
            memDb.Flush();
        }

        [Test]
        public void Can_get_all_ordered()
        {
            MemDb memDb = new();

            memDb.Set(TestItem.KeccakE, _sampleValue);
            memDb.Set(TestItem.KeccakC, _sampleValue);
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            memDb.Set(TestItem.KeccakD, _sampleValue);

            IEnumerable<KeyValuePair<byte[], byte[]?>> orderedItems = memDb.GetAll(true);

            Assert.That(System.Linq.Enumerable.Count(orderedItems), Is.EqualTo(5));

            byte[][] keys = [.. orderedItems.Select(kvp => kvp.Key)];
            for (int i = 0; i < keys.Length - 1; i++)
            {
                Assert.That(Bytes.BytesComparer.Compare(keys[i], keys[i + 1]), Is.LessThan(0), $"Keys should be in ascending order at position {i}");
            }
        }
    }
}
