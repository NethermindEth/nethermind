// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Filters;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Filters;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class LogFilterTests
{
    private int _filterCounter;

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void any_address_filter_matches_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithAnyAddress()
            .Build();

        Assert.That(filter.Matches(Core.Bloom.Empty), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void address_filter_matches_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithAddress(TestItem.AddressA)
            .Build();

        Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressA));

        Assert.That(filter.Matches(bloom), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void addresses_filter_matches_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithAddresses(TestItem.AddressA, TestItem.AddressB, TestItem.AddressC)
            .Build();

        Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressB));

        Assert.That(filter.Matches(bloom), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void any_topics_filter_matches_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithTopicExpressions(TestTopicExpressions.Any)
            .Build();

        Assert.That(filter.Matches(Core.Bloom.Empty), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void specific_topics_filter_matches_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA))
            .Build();

        Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakA, TestItem.KeccakB));

        Assert.That(filter.Matches(bloom), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void multiple_specific_topics_filter_matches_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Specific(TestItem.KeccakB))
            .Build();

        Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressA, TestItem.KeccakA, TestItem.KeccakB));

        Assert.That(filter.Matches(bloom), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void or_topics_filter_matches_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA)))
            .Build();

        Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakB));

        Assert.That(filter.Matches(bloom), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void complex_topics_filter_matches_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA)))
            .Build();

        Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressA, TestItem.KeccakA));

        Assert.That(filter.Matches(bloom), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void complex_filter_matches_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakC)))
            .WithAddress(TestItem.AddressD)
            .Build();

        Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressD, TestItem.KeccakA, TestItem.KeccakC));

        Assert.That(filter.Matches(bloom), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void address_filter_does_not_match_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithAddress(TestItem.AddressA)
            .Build();

        Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressD), GetLogEntry(TestItem.AddressC));

        Assert.That(filter.Matches(bloom), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void addresses_filter_does_not_match_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithAddresses(TestItem.AddressA, TestItem.AddressB, TestItem.AddressC)
            .Build();

        Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressD));

        Assert.That(filter.Matches(bloom), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void specific_topics_filter_does_not_match_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Specific(TestItem.KeccakC))
            .Build();

        Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakA, TestItem.KeccakB));

        Assert.That(filter.Matches(bloom), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void multiple_specific_topics_filter_does_not_match_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Specific(TestItem.KeccakB))
            .Build();

        Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakC));

        Assert.That(filter.Matches(bloom), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void or_topics_filter_does_not_match_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA)))
            .Build();

        Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakC));

        Assert.That(filter.Matches(bloom), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void complex_topics_filter_does_not_match_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA)))
            .Build();

        Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressA, TestItem.KeccakC));

        Assert.That(filter.Matches(bloom), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void complex_filter_does_not_match_bloom()
    {
        LogFilter filter = FilterBuilder.New(ref _filterCounter)
            .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakC)))
            .WithAddress(TestItem.AddressD)
            .Build();

        Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressA, TestItem.KeccakA, TestItem.KeccakD));

        Assert.That(filter.Matches(bloom), Is.False);
    }

    private Core.Bloom GetBloom(params LogEntry[] logEntries)
    {
        Core.Bloom bloom = new();
        bloom.Add(logEntries);
        return bloom;
    }

    private static LogEntry GetLogEntry(Address address, params Hash256[] topics) => new(address, [], topics);
}
