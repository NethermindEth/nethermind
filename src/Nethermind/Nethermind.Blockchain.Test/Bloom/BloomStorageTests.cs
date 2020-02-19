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
        private IBloomConfig _config;
        private MemDb _bloomDb;
        private InMemoryDictionaryFileStoreFactory _fileStoreFactory;


        [SetUp]
        public void SetUp()
        {
            _config = new BloomConfig();
            _bloomDb = new MemDb();
            _fileStoreFactory = new InMemoryDictionaryFileStoreFactory();
        }
        
        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(0, 10)]
        [TestCase(10, 12)]
        public void Empty_storage_does_not_contain_blocks(long from, long to)
        {
            var storage = new BloomStorage(_config, _bloomDb, _fileStoreFactory);
            storage.ContainsRange(from, to).Should().BeFalse();
        }

        [TestCase(0, 0, ExpectedResult = false)]
        [TestCase(1, 1, ExpectedResult = true)]
        [TestCase(0, 10, ExpectedResult = false)]
        [TestCase(1, 10, ExpectedResult = true)]
        [TestCase(10, 12, ExpectedResult = false)]
        public bool Initialized_storage_contain_blocks_as_db(long from, long to)
        {
            var memColumnsDb = _bloomDb;
            memColumnsDb.Set(BloomStorage.MinBlockNumberKey, 1L.ToBigEndianByteArrayWithoutLeadingZeros());
            memColumnsDb.Set(BloomStorage.MaxBlockNumberKey, 11L.ToBigEndianByteArrayWithoutLeadingZeros());
            var storage = new BloomStorage(_config, memColumnsDb, _fileStoreFactory);
            return storage.ContainsRange(from, to);
        }

        [TestCase(0, 0, ExpectedResult = false)]
        [TestCase(1, 1, ExpectedResult = true)]
        [TestCase(0, 10, ExpectedResult = false)]
        [TestCase(1, 10, ExpectedResult = true)]
        [TestCase(10, 12, ExpectedResult = false)]
        public bool Contain_blocks_after_store(long from, long to)
        {
            var storage = new BloomStorage(_config, _bloomDb, _fileStoreFactory);

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
                IEnumerable<long> GetRange(long expectedFound, int offset = 0) => Enumerable.Range(offset, (int) expectedFound).Select(i => (long) i);
                var searchesPerBucket = 1 + LevelMultiplier + LevelMultiplier*LevelMultiplier + LevelMultiplier*LevelMultiplier*LevelMultiplier;
                
                var bucketItems = new BloomStorage(new BloomConfig() {IndexLevelBucketSizes = new []{LevelMultiplier, LevelMultiplier, LevelMultiplier}}, new MemDb(), new InMemoryDictionaryFileStoreFactory()).MaxBucketSize;
                var count = bucketItems*Buckets;
                var maxIndex = count - 1;
                yield return new TestCaseData(0, maxIndex, false, Enumerable.Empty<long>(), Buckets);
                yield return new TestCaseData(0, maxIndex, true, GetRange(count), Buckets * searchesPerBucket);
                yield return new TestCaseData(5, 49, true, GetRange(45, 5), 1 + 1 + 4 + 45); // 1 lookup at top level (16*16**16), 1 lookup at next level (16*16), 4 lookups at next level (16), 45 lookups at bottom level (49-5+1)
                yield return new TestCaseData(0, LevelMultiplier*LevelMultiplier*LevelMultiplier - 1, true, GetRange(LevelMultiplier*LevelMultiplier*LevelMultiplier), searchesPerBucket);
            }
        }

        [TestCaseSource(nameof(GetBloomsTestCases))]
        public void Returns_proper_blooms_after_store(long from, long to, bool isMatch, IEnumerable<long> expectedBlocks, long expectedBloomsChecked)
        {
            var storage = CreateBloomStorage(new BloomConfig() {IndexLevelBucketSizes = new []{LevelMultiplier, LevelMultiplier, LevelMultiplier}});
            long bloomsChecked = 0; 
            
            var bloomEnumeration = storage.GetBlooms(from, to);
            IList<long> ranges = new List<long>();
            foreach (var bloom in bloomEnumeration)
            {
                bloomsChecked++;
                if (isMatch && bloomEnumeration.TryGetBlockNumber(out var blockNumber))
                {
                    ranges.Add(blockNumber);
                }
            }

            ranges.Should().BeEquivalentTo(expectedBlocks);
            bloomsChecked.Should().Be(expectedBloomsChecked);
        }

        private const int Buckets = 3;
        private const int Levels = 3;
        private const int LevelMultiplier = 16;

        private static BloomStorage CreateBloomStorage(BloomConfig bloomConfig = null)
        {
            var storage = new BloomStorage(bloomConfig ?? new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());
            var bucketItems = storage.MaxBucketSize * Buckets;
            
            for (long i = 0; i < bucketItems; i++)
            {
                storage.Store(i, Core.Bloom.Empty);
            }

            return storage;
        }
    }
}