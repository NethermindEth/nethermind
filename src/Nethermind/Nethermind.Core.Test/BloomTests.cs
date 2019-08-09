/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
            BitArray bits = bytes.AsSpan().ToBigEndianBitArray2048();
            Bloom bloom2 = new Bloom(bits);
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

        [Explicit("As bloom filter can have false matches this test can lead to false negatives.")]
        [TestCase(1, 1)]
        [TestCase(1, 10)]
        [TestCase(10, 1)]
        [TestCase(10, 10)]
        [TestCase(100, 1)]
        public void doesnt_match_not_added_item(int count, int topicMax)
        {
            MatchingTest(() => GetLogEntries(count, topicMax), addedEntries => GetLogEntries(count, topicMax), false);
        }
        
        [Test]
        public void empty_doesnt_match_any_item()
        {
            MatchingTest(() => new LogEntry[0], addedEntries => GetLogEntries(100, 10), false);
        }

        public void MatchingTest(Func<LogEntry[]> addedEntries, Func<LogEntry[], LogEntry[]> testedEntries, bool isMatchExpectation)
        {
            Bloom bloom = new Bloom();
            var entries = addedEntries();
            bloom.Add(entries);

            var testEntries = testedEntries(entries);
            var results = testEntries.Select(e => bloom.IsMatch(e));
            results.Should().AllBeEquivalentTo(isMatchExpectation);
        }

        private static LogEntry[] GetLogEntries(int count, int topicsMax)
        {
            var random = new Random();
            var keccakGenerator = GetRandomKeccaks(random);
            var entries = new LogEntry[count];
            for (int i = 0; i < count; i++)
            {
                var topicsCount = random.Next(topicsMax);
                var topics = new Keccak[topicsCount];
                for (int j = 0; j < topicsCount; j++)
                {
                    topics[j] = keccakGenerator.First();
                }

                entries[i] = new LogEntry(Address.FromNumber(random.Next()), Array.Empty<byte>(), topics);
            }

            return entries;
        }

        private static IEnumerable<Keccak> GetRandomKeccaks(Random random)
        {
            byte[] buffer = new byte[32];
            while (true)
            {
                random.NextBytes(buffer);
                yield return Keccak.Compute(buffer);
            }
        }
    }
}