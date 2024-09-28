// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

namespace Nethermind.Blockchain.Test.Filters;

public class FilterStoreTests
{
    [Test, CancelAfter(Timeout.MaxTestTime)]
    public void Can_save_and_load_block_filter()
    {
        FilterStore store = new();
        BlockFilter filter = store.CreateBlockFilter(1);
        store.SaveFilter(filter);
        Assert.That(store.FilterExists(0), Is.True, "exists");
        Assert.That(store.GetFilterType(filter.Id), Is.EqualTo(FilterType.BlockFilter), "type");
    }

    [Test, CancelAfter(Timeout.MaxTestTime)]
    public void Can_save_and_load_log_filter()
    {
        FilterStore store = new();
        LogFilter filter = store.CreateLogFilter(new BlockParameter(1), new BlockParameter(2));
        store.SaveFilter(filter);
        Assert.That(store.FilterExists(0), Is.True, "exists");
        Assert.That(store.GetFilterType(filter.Id), Is.EqualTo(FilterType.LogFilter), "type");
    }

    [Test, CancelAfter(Timeout.MaxTestTime)]
    public void Cannot_overwrite_filters()
    {
        FilterStore store = new();

        BlockFilter externalFilter = new(100, 1);
        store.SaveFilter(externalFilter);
        Assert.Throws<InvalidOperationException>(() => store.SaveFilter(externalFilter));
    }

    [Test, CancelAfter(Timeout.MaxTestTime)]
    public void Ids_are_incremented_when_storing_externally_created_filter()
    {
        FilterStore store = new();

        BlockFilter externalFilter = new(100, 1);
        store.SaveFilter(externalFilter);
        LogFilter filter = store.CreateLogFilter(new BlockParameter(1), new BlockParameter(2));
        store.SaveFilter(filter);

        Assert.That(store.FilterExists(100), Is.True, "exists 100");
        Assert.That(store.FilterExists(101), Is.True,"exists 101");
        Assert.That(store.GetFilterType(filter.Id), Is.EqualTo(FilterType.LogFilter), "type");
    }

    [Test, CancelAfter(Timeout.MaxTestTime)]
    public void Remove_filter_removes_and_notifies()
    {
        FilterStore store = new();
        BlockFilter filter = store.CreateBlockFilter(1);
        store.SaveFilter(filter);
        bool hasNotified = false;
        store.FilterRemoved += (s, e) => hasNotified = true;
        store.RemoveFilter(0);

        Assert.That(hasNotified, Is.True, "notied");
        Assert.That(store.FilterExists(0), Is.False, "exists");
    }

    [Test, CancelAfter(Timeout.MaxTestTime)]
    public void Can_get_filters_by_type()
    {
        FilterStore store = new();
        BlockFilter filter1 = store.CreateBlockFilter(1);
        store.SaveFilter(filter1);
        LogFilter filter2 = store.CreateLogFilter(new BlockParameter(1), new BlockParameter(2));
        store.SaveFilter(filter2);

        LogFilter[] logFilters = store.GetFilters<LogFilter>().ToArray();
        BlockFilter[] blockFilters = store.GetFilters<BlockFilter>().ToArray();

        Assert.That(logFilters.Length, Is.EqualTo(1), "log filters length");
        Assert.That(logFilters[0].Id, Is.EqualTo(1), "log filters ids");
        Assert.That(blockFilters.Length, Is.EqualTo(1), "block Filters length");
        Assert.That(blockFilters[0].Id, Is.EqualTo(0), "block filters ids");
    }

    public static IEnumerable CorrectlyCreatesAddressFilterTestCases
    {
        get
        {
            yield return new TestCaseData(null, AddressFilter.AnyAddress);
            yield return new TestCaseData(TestItem.AddressA.ToString(), new AddressFilter(TestItem.AddressA));
            yield return new TestCaseData(new[] { TestItem.AddressA.ToString(), TestItem.AddressB.ToString() },
                new AddressFilter(new HashSet<AddressAsKey>() { TestItem.AddressA, TestItem.AddressB }));
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

            yield return new TestCaseData(new[] { TestItem.KeccakA.ToString() },
                new SequenceTopicsFilter(new SpecificTopic(TestItem.KeccakA)));

            yield return new TestCaseData(new[] { TestItem.KeccakA.ToString(), TestItem.KeccakB.ToString() },
                new SequenceTopicsFilter(new SpecificTopic(TestItem.KeccakA), new SpecificTopic(TestItem.KeccakB)));

            yield return new TestCaseData(new[] { null, TestItem.KeccakB.ToString() },
                new SequenceTopicsFilter(AnyTopic.Instance, new SpecificTopic(TestItem.KeccakB)));

            yield return new TestCaseData(new object[] { new[] { TestItem.KeccakA.ToString(), TestItem.KeccakB.ToString(), TestItem.KeccakC.ToString() }, TestItem.KeccakD.ToString() },
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
