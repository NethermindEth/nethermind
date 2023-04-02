// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            Bloom bloom = new();
            bloom.Set(Keccak.OfAnEmptyString.Span);
            byte[] bytes = bloom.Bytes.ToArray();
            Bloom bloom2 = new(bytes);
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
            Bloom bloom = new();
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
                int topicsCount = i % topicsMax + 1;
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
