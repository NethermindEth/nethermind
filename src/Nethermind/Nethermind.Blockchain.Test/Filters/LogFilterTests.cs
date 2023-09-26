// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Filters
{
    public class LogFilterTests
    {
        private int _filterCounter;

        [Test, Timeout(Timeout.MaxTestTime)]
        public void any_address_filter_matches_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithAnyAddress()
                .Build();

            filter.Matches(Core.Bloom.Empty).Should().BeTrue();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void address_filter_matches_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithAddress(TestItem.AddressA)
                .Build();

            Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressA));

            filter.Matches(bloom).Should().BeTrue();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void addresses_filter_matches_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithAddresses(TestItem.AddressA, TestItem.AddressB, TestItem.AddressC)
                .Build();

            Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressB));

            filter.Matches(bloom).Should().BeTrue();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void any_topics_filter_matches_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Any)
                .Build();

            filter.Matches(Core.Bloom.Empty).Should().BeTrue();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void specific_topics_filter_matches_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA))
                .Build();

            Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakA, TestItem.KeccakB));

            filter.Matches(bloom).Should().BeTrue();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void multiple_specific_topics_filter_matches_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Specific(TestItem.KeccakB))
                .Build();

            Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressA, TestItem.KeccakA, TestItem.KeccakB));

            filter.Matches(bloom).Should().BeTrue();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void or_topics_filter_matches_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA)))
                .Build();

            Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakB));

            filter.Matches(bloom).Should().BeTrue();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void complex_topics_filter_matches_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA)))
                .Build();

            Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressA, TestItem.KeccakA));

            filter.Matches(bloom).Should().BeTrue();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void complex_filter_matches_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakC)))
                .WithAddress(TestItem.AddressD)
                .Build();

            Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressD, TestItem.KeccakA, TestItem.KeccakC));

            filter.Matches(bloom).Should().BeTrue();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void address_filter_doesnt_match_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithAddress(TestItem.AddressA)
                .Build();

            Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressD), GetLogEntry(TestItem.AddressC));

            filter.Matches(bloom).Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void addresses_filter_doesnt_match_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithAddresses(TestItem.AddressA, TestItem.AddressB, TestItem.AddressC)
                .Build();

            Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressD));

            filter.Matches(bloom).Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void specific_topics_filter_doesnt_match_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Specific(TestItem.KeccakC))
                .Build();

            Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakA, TestItem.KeccakB));

            filter.Matches(bloom).Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void multiple_specific_topics_filter_doesnt_match_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Specific(TestItem.KeccakB))
                .Build();

            Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakC));

            filter.Matches(bloom).Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void or_topics_filter_doesnt_match_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA)))
                .Build();

            Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakC));

            filter.Matches(bloom).Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void complex_topics_filter_doesnt_match_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA)))
                .Build();

            Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressA, TestItem.KeccakC));

            filter.Matches(bloom).Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void complex_filter_doesnt_match_bloom()
        {
            LogFilter filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakC)))
                .WithAddress(TestItem.AddressD)
                .Build();

            Core.Bloom bloom = GetBloom(GetLogEntry(TestItem.AddressA, TestItem.KeccakA, TestItem.KeccakD));

            filter.Matches(bloom).Should().BeFalse();
        }

        private Core.Bloom GetBloom(params LogEntry[] logEntries)
        {
            Core.Bloom bloom = new Core.Bloom();
            bloom.Add(logEntries);
            return bloom;
        }

        private static LogEntry GetLogEntry(Address address, params Keccak[] topics) => new(address, Array.Empty<byte>(), topics);
    }
}
