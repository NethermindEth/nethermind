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
using FluentAssertions;
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
        
        [Test]
        public void any_address_filter_matches_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithAddress(null)
                .Build();

            filter.Matches(Core.Bloom.Empty).Should().BeTrue();
        }
        
        [Test]
        public void address_filter_matches_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithAddress(TestItem.AddressA)
                .Build();

            var bloom = GetBloom(GetLogEntry(TestItem.AddressA));
                
            filter.Matches(bloom).Should().BeTrue();
        }
        
        [Test]
        public void addresses_filter_matches_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithAddresses(TestItem.AddressA, TestItem.AddressB, TestItem.AddressC)
                .Build();

            var bloom = GetBloom(GetLogEntry(TestItem.AddressB));

            filter.Matches(bloom).Should().BeTrue();
        }
        
        [Test]
        public void any_topics_filter_matches_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Any)
                .Build();

            filter.Matches(Core.Bloom.Empty).Should().BeTrue();
        }
        
        [Test]
        public void specific_topics_filter_matches_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA))
                .Build();
            
            var bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakA, TestItem.KeccakB));

            filter.Matches(bloom).Should().BeTrue();
        }
        
        [Test]
        public void multiple_specific_topics_filter_matches_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Specific(TestItem.KeccakB))
                .Build();
            
            var bloom = GetBloom(GetLogEntry(TestItem.AddressA, TestItem.KeccakA, TestItem.KeccakB));

            filter.Matches(bloom).Should().BeTrue();
        }
        
        [Test]
        public void or_topics_filter_matches_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA)))
                .Build();
            
            var bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakB));

            filter.Matches(bloom).Should().BeTrue();
        }
        
        [Test]
        public void complex_topics_filter_matches_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA)))
                .Build();
            
            var bloom = GetBloom(GetLogEntry(TestItem.AddressA, TestItem.KeccakA));

            filter.Matches(bloom).Should().BeTrue();
        }
        
        [Test]
        public void complex_filter_matches_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakC)))
                .WithAddress(TestItem.AddressD)
                .Build();
            
            var bloom = GetBloom(GetLogEntry(TestItem.AddressD, TestItem.KeccakA, TestItem.KeccakC));

            filter.Matches(bloom).Should().BeTrue();
        }
        
        [Test]
        public void address_filter_doesnt_match_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithAddress(TestItem.AddressA)
                .Build();

            var bloom = GetBloom(GetLogEntry(TestItem.AddressD), GetLogEntry(TestItem.AddressC));
                
            filter.Matches(bloom).Should().BeFalse();
        }
        
        [Test]
        public void addresses_filter_doesnt_match_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithAddresses(TestItem.AddressA, TestItem.AddressB, TestItem.AddressC)
                .Build();

            var bloom = GetBloom(GetLogEntry(TestItem.AddressD));

            filter.Matches(bloom).Should().BeFalse();
        }
        
        [Test]
        public void specific_topics_filter_doesnt_match_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Specific(TestItem.KeccakC))
                .Build();
            
            var bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakA, TestItem.KeccakB));

            filter.Matches(bloom).Should().BeFalse();
        }
        
        [Test]
        public void multiple_specific_topics_filter_doesnt_match_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Specific(TestItem.KeccakB))
                .Build();
            
            var bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakC));

            filter.Matches(bloom).Should().BeFalse();
        }
        
        [Test]
        public void or_topics_filter_doesnt_match_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA)))
                .Build();
            
            var bloom = GetBloom(GetLogEntry(TestItem.AddressB, TestItem.KeccakC));

            filter.Matches(bloom).Should().BeFalse();
        }
        
        [Test]
        public void complex_topics_filter_doesnt_match_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakA)))
                .Build();
            
            var bloom = GetBloom(GetLogEntry(TestItem.AddressA, TestItem.KeccakC));

            filter.Matches(bloom).Should().BeFalse();
        }
        
        [Test]
        public void complex_filter_doesnt_match_bloom()
        {
            var filter = FilterBuilder.New(ref _filterCounter)
                .WithTopicExpressions(TestTopicExpressions.Specific(TestItem.KeccakA), TestTopicExpressions.Or(TestTopicExpressions.Specific(TestItem.KeccakB), TestTopicExpressions.Specific(TestItem.KeccakC)))
                .WithAddress(TestItem.AddressD)
                .Build();
            
            var bloom = GetBloom(GetLogEntry(TestItem.AddressA, TestItem.KeccakA, TestItem.KeccakD));

            filter.Matches(bloom).Should().BeFalse();
        }

        private Core.Bloom GetBloom(params LogEntry[] logEntries)
        {
            var bloom = new Core.Bloom();
            bloom.Add(logEntries);
            return bloom;
        }

        private static LogEntry GetLogEntry(Address address, params Keccak[] topics) => new LogEntry(address, Array.Empty<byte>(), topics);
    }
}
