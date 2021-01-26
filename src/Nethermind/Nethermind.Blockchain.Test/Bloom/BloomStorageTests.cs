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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using NSubstitute;
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
                yield return new TestCaseData(5, 49, true, GetRange(45, 5), 4 + 45); // 4 lookups at level one (16), 45 lookups at bottom level (49-5+1)
                yield return new TestCaseData(0, LevelMultiplier*LevelMultiplier*LevelMultiplier - 1, true, GetRange(LevelMultiplier*LevelMultiplier*LevelMultiplier), searchesPerBucket - 1); // skips highest level
                yield return new TestCaseData(0, LevelMultiplier*LevelMultiplier*LevelMultiplier * 2 - 1, true, GetRange(LevelMultiplier*LevelMultiplier*LevelMultiplier * 2), (searchesPerBucket - 1) * 2); // skips highest level
                yield return new TestCaseData(0, LevelMultiplier*LevelMultiplier*LevelMultiplier * 3 - 1, true, GetRange(LevelMultiplier*LevelMultiplier*LevelMultiplier * 3), searchesPerBucket * 3); // doesn't skip highest level
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
        
        [TestCase(1, 10, new long[] {4}, new int[] {4})]
        [TestCase(0, 4, new long[] {4}, new int[] {4})]
        [TestCase(1, 10, new long[] {1, 4, 6, 8}, new int[] {4})]
        [TestCase(1, 10, new long[] {4, 6, 8}, new int[] {4, 4})]
        [TestCase(1, 10, new long[] {4, 8, 16, 32}, new int[] {4, 4})]
        [TestCase(1, 48, new long[] {4, 8, 16, 32}, new int[] {4, 4})]
        [TestCase(5, 60, new long[] {4, 8, 49}, new int[] {8, 3})]
        [TestCase(1, 120, new long[] {4, 8, 64, 65}, new int[] {4, 4, 4})]
        [TestCase(0, 120, new long[] {0, 1, 2, 3, 5, 7, 11, 120}, new int[] {9, 3})]
        public void Can_find_bloom_with_fromBlock_offset(long from, long to, long[] blocksSet, int[] levels)
        {
            var storage = CreateBloomStorage(new BloomConfig() {IndexLevelBucketSizes = levels});
            var bloom = new Core.Bloom();
            byte[] bytes = {1, 2, 3};
            bloom.Set(bytes);
            foreach (var blockNumber in blocksSet)
            {
                if (blockNumber > storage.MaxBlockNumber + 1)
                {
                    // Assert.Fail($"Missing blocks. Trying inserting {blockNumber}, when current max block is {storage.MaxBlockNumber}.");
                }
                storage.Store(blockNumber, bloom);
            }
            
            var bloomEnumeration = storage.GetBlooms(from, to);
            IList<long> foundBlocks = new List<long>(blocksSet.Length);
            foreach (var b in bloomEnumeration)
            {
                if (b.Matches(bytes) && bloomEnumeration.TryGetBlockNumber(out var block))
                {
                    foundBlocks.Add(block);
                }
            }

            var expectedFoundBlocks = blocksSet.Where(b => b >= from && b <= to).ToArray();
            TestContext.Out.WriteLine($"Expected found blocks: {string.Join(", ", expectedFoundBlocks)}");
            foundBlocks.Should().BeEquivalentTo(expectedFoundBlocks);
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
        
        private class MockFileStore : IFileStore
        {
            public void Dispose()
            {
                
            }

            public void Write(long index, ReadOnlySpan<byte> element)
            {
                
            }

            public int Read(long index, Span<byte> element)
            {
                return Core.Bloom.ByteLength;
            }

            public IFileReader CreateFileReader()
            {
                return new InMemoryDictionaryFileReader(this);
            }

            public int Flushes { get; private set; }
        }
        
        [Test]
        public void Can_safely_insert_concurrently()
        {
            _config.IndexLevelBucketSizes = new[]{byte.MaxValue + 1};
            var storage = new BloomStorage(_config, _bloomDb, _fileStoreFactory);
            Core.Bloom expectedBloom = new Core.Bloom();
            for (int i = 0; i <= byte.MaxValue; i++)
            {
                expectedBloom.Set(i);
            }

            Parallel.For(0, byte.MaxValue * byte.MaxValue * 2, i =>
            {
                var bloom = new Core.Bloom();
                bloom.Set(i % Core.Bloom.BitLength);
                storage.Store(i, bloom);
            });

            var first = storage.GetBlooms(0, byte.MaxValue * 3).First();
            first.Should().Be(expectedBloom);
        }
    }
}
