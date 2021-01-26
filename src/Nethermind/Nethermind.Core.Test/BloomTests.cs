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
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class BloomTests
    {
        [Test]
        public void Test()
        {
            Bloom bloom = new Bloom();
            bloom.Set(Keccak.OfAnEmptyString.Bytes);
            byte[] bytes = bloom.Bytes;
            Bloom bloom2 = new Bloom(bytes);
            Assert.AreEqual(bloom.ToString(), bloom2.ToString());
        }

        [TestCase(1, 1)]
        [TestCase(1, 10)]
        [TestCase(10, 1)]
        [TestCase(10, 10)]
        [TestCase(100, 1)]
        public void matches_previously_added_item(int count, int topicMax)
        {
            MatchingTest(() => GetLogEntries(count, topicMax), addedEntries => addedEntries, true);
        }

        [TestCase(1, 1)]
        [TestCase(1, 10)]
        [TestCase(10, 1)]
        [TestCase(10, 10)]
        [TestCase(100, 1)]
        public void doesnt_match_not_added_item(int count, int topicMax)
        {
            MatchingTest(() => GetLogEntries(count, topicMax),
                addedEntries => GetLogEntries(count, topicMax, 
                addedEntries.Sum(a => a.Topics.Length)), false);
        }
        
        [Test]
        public void empty_doesnt_match_any_item()
        {
            MatchingTest(Array.Empty<LogEntry>, addedEntries => GetLogEntries(100, 10), false);
        }

        public void MatchingTest(Func<LogEntry[]> addedEntries, Func<LogEntry[], LogEntry[]> testedEntries, bool isMatchExpectation)
        {
            Bloom bloom = new Bloom();
            var entries = addedEntries();
            bloom.Add(entries);

            var testEntries = testedEntries(entries);
            var results = testEntries.Select(e => bloom.Matches(e));
            results.Should().AllBeEquivalentTo(isMatchExpectation);
        }

        private static LogEntry[] GetLogEntries(int count, int topicsMax, int start = 0)
        {
            var keccakGenerator = TestItem.Keccaks;
            var entries = new LogEntry[count];
            for (int i = start; i < count + start; i++)
            {
                var topicsCount = i % topicsMax + 1;
                var topics = new Keccak[topicsCount];
                for (int j = 0; j < topicsCount; j++)
                {
                    topics[j] = keccakGenerator[i + j];
                }

                entries[i - start] = new LogEntry(TestItem.Addresses[i], Array.Empty<byte>(), topics);
            }

            return entries;
        }
    }
}
