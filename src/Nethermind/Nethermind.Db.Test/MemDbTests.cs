//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [TestFixture]
    public class MemDbTests
    {
        [Test]
        public void Simple_set_get_is_fine()
        {
            IDb memDb = new MemDb();
            byte[] bytes = new byte[] {1, 2, 3};
            memDb.Set(TestItem.KeccakA, bytes);
            byte[] retrievedBytes = memDb.Get(TestItem.KeccakA);
            retrievedBytes.Should().BeEquivalentTo(bytes);
        }

        private byte[] _sampleValue = {1, 2, 3};

        [Test]
        public void Can_create_with_delays()
        {
            MemDb memDb = new MemDb(10, 10);
            memDb.Set(TestItem.KeccakA, new byte[] {1, 2, 3});
            memDb.Get(TestItem.KeccakA);
            var some = memDb[new[] {TestItem.KeccakA.Bytes}];
        }

        [Test]
        public void Can_create_with_name()
        {
            MemDb memDb = new MemDb("desc");
            memDb.Set(TestItem.KeccakA, new byte[] {1, 2, 3});
            memDb.Get(TestItem.KeccakA);
            memDb.Name.Should().Be("desc");
        }

        [Test]
        public void Can_create_without_arguments()
        {
            MemDb memDb = new MemDb();
            memDb.Set(TestItem.KeccakA, new byte[] {1, 2, 3});
            memDb.Get(TestItem.KeccakA);
        }

        [Test]
        public void Can_use_batches_without_issues()
        {
            MemDb memDb = new MemDb();
            using (memDb.StartBatch())
            {
                memDb.Set(TestItem.KeccakA, _sampleValue);
            }

            byte[] retrieved = memDb.Get(TestItem.KeccakA);
            retrieved.Should().BeEquivalentTo(_sampleValue);
        }

        [Test]
        public void Can_delete()
        {
            MemDb memDb = new MemDb();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            memDb.Clear();
            memDb.Keys.Should().HaveCount(0);
        }

        [Test]
        public void Can_check_if_key_exists()
        {
            MemDb memDb = new MemDb();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.KeyExists(TestItem.KeccakA).Should().BeTrue();
            memDb.KeyExists(TestItem.KeccakB).Should().BeFalse();
        }

        [Test]
        public void Can_remove_key()
        {
            MemDb memDb = new MemDb();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Remove(TestItem.KeccakA.Bytes);
            memDb.KeyExists(TestItem.KeccakA).Should().BeFalse();
        }

        [Test]
        public void Can_get_keys()
        {
            MemDb memDb = new MemDb();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            memDb.Keys.Should().HaveCount(2);
        }

        [Test]
        public void Can_get_some_keys()
        {
            MemDb memDb = new MemDb();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            var result = memDb[new[] {TestItem.KeccakB.Bytes, TestItem.KeccakB.Bytes, TestItem.KeccakC.Bytes}];
            result.Should().HaveCount(3);
            result[0].Value.Should().NotBeNull();
            result[1].Value.Should().NotBeNull();
            result[2].Value.Should().BeNull();
        }

        [Test]
        public void Can_get_all()
        {
            MemDb memDb = new MemDb();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            memDb.GetAllValues().Should().HaveCount(2);
        }

        [Test]
        public void Can_get_values()
        {
            MemDb memDb = new MemDb();
            memDb.Set(TestItem.KeccakA, _sampleValue);
            memDb.Set(TestItem.KeccakB, _sampleValue);
            memDb.Values.Should().HaveCount(2);
        }

        [Test]
        public void Dispose_does_not_cause_trouble()
        {
            MemDb memDb = new MemDb();
            memDb.Dispose();
        }

        [Test]
        public void Flush_does_not_cause_trouble()
        {
            MemDb memDb = new MemDb();
            memDb.Flush();
        }

        [Test]
        public void Innermost_is_self()
        {
            MemDb memDb = new MemDb();
            memDb.Innermost.Should().BeSameAs(memDb);
        }
    }
}
