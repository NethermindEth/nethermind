//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Blockchain.Bloom;
using Nethermind.Core.Extensions;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Bloom
{
    public class BloomStorageTests
    {
        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(0, 10)]
        [TestCase(10, 12)]
        public void Empty_storage_does_not_contain_blocks(long from, long to)
        {
            var storage = new BloomStorage(BloomDb);
            storage.ContainsRange(from, to).Should().BeFalse();
        }

        private static MemColumnsDb<byte> BloomDb => new MemColumnsDb<byte>(1, 2, 3);

        [TestCase(0, 0, ExpectedResult = false)]
        [TestCase(1, 1, ExpectedResult = true)]
        [TestCase(0, 10, ExpectedResult = false)]
        [TestCase(1, 10, ExpectedResult = true)]
        [TestCase(10, 12, ExpectedResult = false)]
        public bool Initialized_storage_contain_blocks_as_db(long from, long to)
        {
            var memColumnsDb = BloomDb;
            memColumnsDb.Set(BloomStorage.MinBlockNumberKey, 1L.ToBigEndianByteArrayWithoutLeadingZeros());
            memColumnsDb.Set(BloomStorage.MaxBlockNumberKey, 11L.ToBigEndianByteArrayWithoutLeadingZeros());
            var storage = new BloomStorage(memColumnsDb);
            return storage.ContainsRange(from, to);
        }

        [TestCase(0, 0, ExpectedResult = false)]
        [TestCase(1, 1, ExpectedResult = true)]
        [TestCase(0, 10, ExpectedResult = false)]
        [TestCase(1, 10, ExpectedResult = true)]
        [TestCase(10, 12, ExpectedResult = false)]
        public bool Contain_blocks_after_store(long from, long to)
        {
            var storage = new BloomStorage(BloomDb);

            for (long i = 1; i < 11; i++)
            {
                storage.Store(i, Core.Bloom.Empty);    
            }
            
            return storage.ContainsRange(from, to);
        }

        public static IEnumerable GetBloomsTestCases
        {
            get
            {
                IEnumerable<Range<long>> CalcRanges(long expectedFound) => Enumerable.Range(0, (int) (expectedFound / LevelMultiplier)).Select(i => new Range<long>(i * LevelMultiplier, (i + 1) * LevelMultiplier - 1));
                
                var bucketItems = GetBucketItems(Levels, LevelMultiplier);
                var count = bucketItems*Buckets;
                var maxIndex = count - 1;
                yield return new TestCaseData(0, maxIndex, false, Enumerable.Empty<Range<long>>(), Buckets);
                yield return new TestCaseData(0, maxIndex, true, CalcRanges(count), Buckets * (1 + LevelMultiplier + LevelMultiplier*LevelMultiplier));
                yield return new TestCaseData(5, 10, true, new[] {new Range<long>(5, 10)}, 1 + 1 + 1);
                yield return new TestCaseData(0, LevelMultiplier*LevelMultiplier*LevelMultiplier - 1, true, CalcRanges(LevelMultiplier*LevelMultiplier*LevelMultiplier), 1 + LevelMultiplier + LevelMultiplier*LevelMultiplier);
            }
        }

        [TestCaseSource(nameof(GetBloomsTestCases))]
        public void Returns_proper_blooms_after_store(long from, long to, bool isMatch, IEnumerable<Range<long>> expectedRanges, long expectedBloomsChecked)
        {
            var storage = CreateBloomStorage();
            long bloomsChecked = 0; 
            
            var bloomEnumeration = storage.GetBlooms(from, to);
            IList<Range<long>> ranges = new List<Range<long>>();
            foreach (var bloom in bloomEnumeration)
            {
                bloomsChecked++;
                if (isMatch && bloomEnumeration.TryGetBlockRange(out var range))
                {
                    ranges.Add(range);
                }
            }

            ranges.Should().BeEquivalentTo(expectedRanges);
            bloomsChecked.Should().Be(expectedBloomsChecked);
        }

        private const int Buckets = 3;
        private const int Levels = 3;
        private const int LevelMultiplier = 16;

        private static BloomStorage CreateBloomStorage()
        {
            var storage = new BloomStorage(BloomDb, LevelMultiplier);
            var bucketItems = GetBucketItems(storage.Levels, storage.LevelMultiplier) * Buckets;
            
            for (long i = 0; i < bucketItems; i++)
            {
                storage.Store(i, Core.Bloom.Empty);
            }

            return storage;
        }

        private static long GetBucketItems(int levels, int levelMultiplier) => (long) Math.Pow(levelMultiplier, levels);
    }
}