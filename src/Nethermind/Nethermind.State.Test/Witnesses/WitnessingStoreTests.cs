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
// 

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Witnesses;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test.Witnesses
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class WitnessingStoreTests
    {
        [Test]
        public void Collects_on_reads()
        {
            Context context = new Context();
            context.Wrapped[Key1].Returns(Value1);
            _ = context.Database[Key1];
            context.WitnessCollector.Collected.Should().HaveCount(1);
        }
        
        [Test]
        public void Collects_on_reads_when_cached_underneath()
        {
            Context context = new Context(2);
            context.Wrapped[Key1].Returns(Value1);
            context.Wrapped[Key2].Returns(Value2);
            context.Wrapped[Key3].Returns(Value3);
            _ = context.Database[Key1];
            _ = context.Database[Key2];
            _ = context.Database[Key3];
            context.WitnessCollector.Collected.Should().HaveCount(3);
            context.WitnessCollector.Reset();
            _ = context.Database[Key1];
            _ = context.Database[Key2];
            _ = context.Database[Key3];
            context.WitnessCollector.Collected.Should().HaveCount(3);
        }
        
        [Test]
        public void Collects_on_reads_when_cached_underneath_and_previously_populated()
        {
            Context context = new Context(3);
            context.Database[Key1] = Value1;
            context.Database[Key2] = Value1;
            context.Database[Key3] = Value1;
            context.WitnessCollector.Collected.Should().HaveCount(0);
            _ = context.Database[Key1];
            _ = context.Database[Key2];
            _ = context.Database[Key3];
            context.WitnessCollector.Collected.Should().HaveCount(3);
        }
        
        [Test]
        public void Does_not_collect_on_writes()
        {
            Context context = new Context();
            context.Database[Key1] = Value1;
            context.WitnessCollector.Collected.Should().HaveCount(0);
        }
        
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(31)]
        [TestCase(33)]
        public void Only_works_with_32_bytes_keys(int keyLength)
        {
            Context context = new Context();
            context.Wrapped[null].ReturnsForAnyArgs(Bytes.Empty);
            Assert.Throws<NotSupportedException>(
                () => _ = context.Database[new byte[keyLength]]);
        }
        
        private class Context
        {
            public IKeyValueStoreWithBatching Wrapped { get; } = Substitute.For<IKeyValueStoreWithBatching>();

            public WitnessingStore Database { get; }
            
            public IWitnessCollector WitnessCollector { get; }

            public Context()
            {
                WitnessCollector = new WitnessCollector(new MemDb(), LimboLogs.Instance);
                Database = new WitnessingStore(Wrapped, WitnessCollector);
            }
            
            public Context(int cacheSize)
            {
                WitnessCollector = new WitnessCollector(new MemDb(), LimboLogs.Instance);
                Database = new WitnessingStore(new CachingStore(Wrapped, cacheSize), WitnessCollector);
            }
        }

        private static readonly byte[] Key1 = TestItem.KeccakA.Bytes;

        private static readonly byte[] Key2 = TestItem.KeccakB.Bytes;

        private static readonly byte[] Key3 = TestItem.KeccakC.Bytes;

        private static readonly byte[] Value1 = {1};

        private static readonly byte[] Value2 = {2};

        private static readonly byte[] Value3 = {3};
    }
}
