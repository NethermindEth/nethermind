// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
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
            memDb.Set(TestItem._commitmentA, bytes);
            byte[] retrievedBytes = memDb.Get(TestItem._commitmentA);
            retrievedBytes.Should().BeEquivalentTo(bytes);
        }

        private byte[] _sampleValue = { 1, 2, 3 };

        [Test]
        public void Can_create_with_delays()
        {
            MemDb memDb = new(10, 10);
            memDb.Set(TestItem._commitmentA, new byte[] { 1, 2, 3 });
            memDb.Get(TestItem._commitmentA);
            KeyValuePair<byte[], byte[]>[] some = memDb[new[] { TestItem._commitmentA.BytesToArray() }];
        }

        [Test]
        public void Can_create_with_name()
        {
            MemDb memDb = new("desc");
            memDb.Set(TestItem._commitmentA, new byte[] { 1, 2, 3 });
            memDb.Get(TestItem._commitmentA);
            memDb.Name.Should().Be("desc");
        }

        [Test]
        public void Can_create_without_arguments()
        {
            MemDb memDb = new();
            memDb.Set(TestItem._commitmentA, new byte[] { 1, 2, 3 });
            memDb.Get(TestItem._commitmentA);
        }

        [Test]
        public void Can_use_batches_without_issues()
        {
            MemDb memDb = new();
            using (memDb.StartBatch())
            {
                memDb.Set(TestItem._commitmentA, _sampleValue);
            }

            byte[] retrieved = memDb.Get(TestItem._commitmentA);
            retrieved.Should().BeEquivalentTo(_sampleValue);
        }

        [Test]
        public void Can_delete()
        {
            MemDb memDb = new();
            memDb.Set(TestItem._commitmentA, _sampleValue);
            memDb.Set(TestItem._commitmentB, _sampleValue);
            memDb.Clear();
            memDb.Keys.Should().HaveCount(0);
        }

        [Test]
        public void Can_check_if_key_exists()
        {
            MemDb memDb = new();
            memDb.Set(TestItem._commitmentA, _sampleValue);
            memDb.KeyExists(TestItem._commitmentA).Should().BeTrue();
            memDb.KeyExists(TestItem._commitmentB).Should().BeFalse();
        }

        [Test]
        public void Can_remove_key()
        {
            MemDb memDb = new();
            memDb.Set(TestItem._commitmentA, _sampleValue);
            memDb.Remove(TestItem._commitmentA.Bytes);
            memDb.KeyExists(TestItem._commitmentA).Should().BeFalse();
        }

        [Test]
        public void Can_get_keys()
        {
            MemDb memDb = new();
            memDb.Set(TestItem._commitmentA, _sampleValue);
            memDb.Set(TestItem._commitmentB, _sampleValue);
            memDb.Keys.Should().HaveCount(2);
        }

        [Test]
        public void Can_get_some_keys()
        {
            MemDb memDb = new();
            memDb.Set(TestItem._commitmentA, _sampleValue);
            memDb.Set(TestItem._commitmentB, _sampleValue);
            KeyValuePair<byte[], byte[]>[] result = memDb[new[] { TestItem._commitmentB.BytesToArray(), TestItem._commitmentB.BytesToArray(), TestItem._commitmentC.BytesToArray() }];
            result.Should().HaveCount(3);
            result[0].Value.Should().NotBeNull();
            result[1].Value.Should().NotBeNull();
            result[2].Value.Should().BeNull();
        }

        [Test]
        public void Can_get_all()
        {
            MemDb memDb = new();
            memDb.Set(TestItem._commitmentA, _sampleValue);
            memDb.Set(TestItem._commitmentB, _sampleValue);
            memDb.GetAllValues().Should().HaveCount(2);
        }

        [Test]
        public void Can_get_values()
        {
            MemDb memDb = new();
            memDb.Set(TestItem._commitmentA, _sampleValue);
            memDb.Set(TestItem._commitmentB, _sampleValue);
            memDb.Values.Should().HaveCount(2);
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
    }
}
