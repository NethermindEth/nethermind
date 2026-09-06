// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Filters;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class FilterStoreTests
{
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_save_and_load_block_filter()
    {
        FilterStore store = new(new TimerFactory());
        BlockFilter filter = store.CreateBlockFilter();
        store.SaveFilter(filter);
        Assert.That(store.FilterExists(0), Is.True, "exists");
        Assert.That(store.GetFilterType(filter.Id), Is.EqualTo(FilterType.BlockFilter), "type");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_save_and_load_log_filter()
    {
        FilterStore store = new(new TimerFactory());
        LogFilter filter = store.CreateLogFilter(new BlockParameter(1), new BlockParameter(2));
        store.SaveFilter(filter);
        Assert.That(store.FilterExists(0), Is.True, "exists");
        Assert.That(store.GetFilterType(filter.Id), Is.EqualTo(FilterType.LogFilter), "type");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Cannot_overwrite_filters()
    {
        FilterStore store = new(new TimerFactory());

        BlockFilter externalFilter = new(100);
        store.SaveFilter(externalFilter);
        Assert.Throws<InvalidOperationException>(() => store.SaveFilter(externalFilter));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Ids_are_incremented_when_storing_externally_created_filter()
    {
        FilterStore store = new(new TimerFactory());

        BlockFilter externalFilter = new(100);
        store.SaveFilter(externalFilter);
        LogFilter filter = store.CreateLogFilter(new BlockParameter(1), new BlockParameter(2));
        store.SaveFilter(filter);

        Assert.That(store.FilterExists(100), Is.True, "exists 100");
        Assert.That(store.FilterExists(101), Is.True, "exists 101");
        Assert.That(store.GetFilterType(filter.Id), Is.EqualTo(FilterType.LogFilter), "type");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Remove_filter_removes_and_notifies()
    {
        FilterStore store = new(new TimerFactory());
        BlockFilter filter = store.CreateBlockFilter();
        store.SaveFilter(filter);
        bool hasNotified = false;
        store.FilterRemoved += (s, e) => hasNotified = true;
        store.RemoveFilter(0);

        Assert.That(hasNotified, Is.True, "notified");
        Assert.That(store.FilterExists(0), Is.False, "exists");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_get_filters_by_type()
    {
        FilterStore store = new(new TimerFactory());
        BlockFilter filter1 = store.CreateBlockFilter();
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
            yield return new TestCaseData(null, AddressFilter.AnyAddress)
                .SetName("Correctly_creates_address_filter_any_address");
            yield return new TestCaseData(new HashSet<AddressAsKey> { new(TestItem.AddressA) }, new AddressFilter(TestItem.AddressA))
                .SetName("Correctly_creates_address_filter_single_address");
            yield return new TestCaseData(new HashSet<AddressAsKey> { new(TestItem.AddressA), new(TestItem.AddressB) },
                new AddressFilter([TestItem.AddressA, TestItem.AddressB]))
                .SetName("Correctly_creates_address_filter_multiple_addresses");
        }
    }

    [TestCaseSource(nameof(CorrectlyCreatesAddressFilterTestCases))]
    public void Correctly_creates_address_filter(HashSet<AddressAsKey> address, AddressFilter expected)
    {
        BlockParameter from = new(100);
        BlockParameter to = new(BlockParameterType.Latest);
        FilterStore store = new(new TimerFactory());
        LogFilter filter = store.CreateLogFilter(from, to, address);
        Assert.That(filter.AddressFilter.Addresses, Is.EquivalentTo(expected.Addresses));
    }

    public static IEnumerable CorrectlyCreatesTopicsFilterTestCases
    {
        get
        {
            yield return new TestCaseData(null, SequenceTopicsFilter.AnyTopic)
                .SetName("Correctly_creates_topics_filter_any_topic");

            yield return new TestCaseData(new[] { new[] { TestItem.KeccakA } },
                new SequenceTopicsFilter(new SpecificTopic(TestItem.KeccakA)))
                .SetName("Correctly_creates_topics_filter_single_topic");

            yield return new TestCaseData(new[] { new[] { TestItem.KeccakA }, new[] { TestItem.KeccakB } },
                new SequenceTopicsFilter(new SpecificTopic(TestItem.KeccakA), new SpecificTopic(TestItem.KeccakB)))
                .SetName("Correctly_creates_topics_filter_topic_sequence");

            yield return new TestCaseData(new[] { null, new[] { TestItem.KeccakB } },
                new SequenceTopicsFilter(AnyTopic.Instance, new SpecificTopic(TestItem.KeccakB)))
                .SetName("Correctly_creates_topics_filter_any_then_specific");

            yield return new TestCaseData(
                new[] { new[] { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC }, new[] { TestItem.KeccakD } },
                new SequenceTopicsFilter(
                    new OrExpression(new SpecificTopic(TestItem.KeccakA), new SpecificTopic(TestItem.KeccakB),
                        new SpecificTopic(TestItem.KeccakC)), new SpecificTopic(TestItem.KeccakD)))
                .SetName("Correctly_creates_topics_filter_or_then_specific");
        }
    }

    [TestCaseSource(nameof(CorrectlyCreatesTopicsFilterTestCases))]
    public void Correctly_creates_topics_filter(Hash256[]?[]? topics, TopicsFilter expected)
    {
        BlockParameter from = new(100);
        BlockParameter to = new(BlockParameterType.Latest);
        FilterStore store = new(new TimerFactory());
        LogFilter filter = store.CreateLogFilter(from, to, null, topics);
        Assert.That(filter.TopicsFilter, Is.EqualTo(expected).Using<TopicsFilter>(TopicsFiltersEqual));
    }

    private static bool TopicsFiltersEqual(TopicsFilter? actual, TopicsFilter? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        return actual.GetType() == expected.GetType()
            && actual.AcceptsAnyBlock == expected.AcceptsAnyBlock
            && actual.Expressions.SequenceEqual(expected.Expressions);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    [Parallelizable(ParallelScope.None)]
    public async Task CleanUps_filters()
    {
        List<int> removedFilterIds = [];
        FilterStore store = new(new TimerFactory(), 500, 100);
        store.FilterRemoved += (_, e) => removedFilterIds.Add(e.FilterId);
        store.SaveFilter(store.CreateBlockFilter());
        store.SaveFilter(store.CreateBlockFilter());
        store.SaveFilter(store.CreateLogFilter(BlockParameter.Earliest, BlockParameter.Latest));
        store.SaveFilter(store.CreatePendingTransactionFilter());
        await Task.Delay(300);
        store.RefreshFilter(0);
        await Task.Delay(300);
        store.RefreshFilter(0);
        Assert.That(() => store.FilterExists(0), Is.True.After(300, 10), "filter 0 exists");
        Assert.That(() => store.FilterExists(1), Is.False.After(300, 10), "filter 1 doesn't exist");
        Assert.That(() => store.FilterExists(2), Is.False.After(300, 10), "filter 2 doesn't exist");
        Assert.That(() => store.FilterExists(3), Is.False.After(300, 10), "filter 3 doesn't exist");
        store.RefreshFilter(0);
        Assert.That(() => removedFilterIds, Is.EqualTo([1, 2, 3]).After(300, 10));
    }
}
