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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Filters
{
    [TestFixture]
    public class FilterStoreTests
    {
        [Test]
        public void Can_save_and_load_block_filter()
        {
            FilterStore store = new();
            BlockFilter filter = store.CreateBlockFilter(1);
            store.SaveFilter(filter);
            Assert.True(store.FilterExists(0), "exists");
            Assert.AreEqual(FilterType.BlockFilter, store.GetFilterType(filter.Id), "type");
        }

        [Test]
        public void Can_save_and_load_log_filter()
        {
            FilterStore store = new();
            LogFilter filter = store.CreateLogFilter(new BlockParameter(1), new BlockParameter(2));
            store.SaveFilter(filter);
            Assert.True(store.FilterExists(0), "exists");
            Assert.AreEqual(FilterType.LogFilter, store.GetFilterType(filter.Id), "type");
        }

        [Test]
        public void Cannot_overwrite_filters()
        {
            FilterStore store = new();

            BlockFilter externalFilter = new(100, 1);
            store.SaveFilter(externalFilter);
            Assert.Throws<InvalidOperationException>(() => store.SaveFilter(externalFilter));
        }

        [Test]
        public void Ids_are_incremented_when_storing_externally_created_filter()
        {
            FilterStore store = new();

            BlockFilter externalFilter = new(100, 1);
            store.SaveFilter(externalFilter);
            LogFilter filter = store.CreateLogFilter(new BlockParameter(1), new BlockParameter(2));
            store.SaveFilter(filter);

            Assert.True(store.FilterExists(100), "exists 100");
            Assert.True(store.FilterExists(101), "exists 101");
            Assert.AreEqual(FilterType.LogFilter, store.GetFilterType(filter.Id), "type");
        }

        [Test]
        public void Remove_filter_removes_and_notifies()
        {
            FilterStore store = new();
            BlockFilter filter = store.CreateBlockFilter(1);
            store.SaveFilter(filter);
            bool hasNotified = false;
            store.FilterRemoved += (s, e) => hasNotified = true;
            store.RemoveFilter(0);

            Assert.True(hasNotified, "notied");
            Assert.False(store.FilterExists(0), "exists");
        }
        
        [Test]
        public void Can_get_filters_by_type()
        {
            FilterStore store = new();
            BlockFilter filter1 = store.CreateBlockFilter(1);
            store.SaveFilter(filter1);
            LogFilter filter2 = store.CreateLogFilter(new BlockParameter(1), new BlockParameter(2));
            store.SaveFilter(filter2);

            LogFilter[] logFilters = store.GetFilters<LogFilter>().ToArray();
            BlockFilter[] blockFilters = store.GetFilters<BlockFilter>().ToArray();
            
            Assert.AreEqual(1, logFilters.Length, "log filters length");
            Assert.AreEqual(1, logFilters[0].Id, "log filters ids");
            Assert.AreEqual(1, blockFilters.Length, "block Filters length");
            Assert.AreEqual(0, blockFilters[0].Id, "block filters ids");
        }
        
        public static IEnumerable CorrectlyCreatesAddressFilterTestCases
        {
            get
            {
                yield return new TestCaseData(null, AddressFilter.AnyAddress);
                yield return new TestCaseData(TestItem.AddressA.ToString(), new AddressFilter(TestItem.AddressA));
                yield return new TestCaseData(new[] {TestItem.AddressA.ToString(), TestItem.AddressB.ToString()},
                    new AddressFilter(new HashSet<Address>() {TestItem.AddressA, TestItem.AddressB}));
            }
        }
        
        [TestCaseSource(nameof(CorrectlyCreatesAddressFilterTestCases))]
        public void Correctly_creates_address_filter(object address, AddressFilter expected)
        {
            BlockParameter from = new(100);
            BlockParameter to = new(BlockParameterType.Latest);
            FilterStore store = new();
            LogFilter filter = store.CreateLogFilter(from, to, address);
            filter.AddressFilter.Should().BeEquivalentTo(expected);
        }
        
        public static IEnumerable CorrectlyCreatesTopicsFilterTestCases
        {
            get
            {
                yield return new TestCaseData(null, SequenceTopicsFilter.AnyTopic);
                
                yield return new TestCaseData(new[] {TestItem.KeccakA.ToString()}, 
                    new SequenceTopicsFilter(new SpecificTopic(TestItem.KeccakA)));
                
                yield return new TestCaseData(new[] {TestItem.KeccakA.ToString(), TestItem.KeccakB.ToString()}, 
                    new SequenceTopicsFilter(new SpecificTopic(TestItem.KeccakA), new SpecificTopic(TestItem.KeccakB)));
                
                yield return new TestCaseData(new[] {null, TestItem.KeccakB.ToString()}, 
                    new SequenceTopicsFilter(AnyTopic.Instance, new SpecificTopic(TestItem.KeccakB)));
                
                yield return new TestCaseData(new object[] {new[] {TestItem.KeccakA.ToString(), TestItem.KeccakB.ToString(), TestItem.KeccakC.ToString()}, TestItem.KeccakD.ToString()}, 
                    new SequenceTopicsFilter(new OrExpression(new SpecificTopic(TestItem.KeccakA), new SpecificTopic(TestItem.KeccakB), new SpecificTopic(TestItem.KeccakC)), new SpecificTopic(TestItem.KeccakD)));
            }
        }
        
        [TestCaseSource(nameof(CorrectlyCreatesTopicsFilterTestCases))]
        public void Correctly_creates_topics_filter(IEnumerable<object> topics, TopicsFilter expected)
        {
            BlockParameter from = new(100);
            BlockParameter to = new(BlockParameterType.Latest);
            FilterStore store = new();
            LogFilter filter = store.CreateLogFilter(from, to, null, topics);
            filter.TopicsFilter.Should().BeEquivalentTo(expected, c => c.ComparingByValue<TopicsFilter>());
        }
    }
}
