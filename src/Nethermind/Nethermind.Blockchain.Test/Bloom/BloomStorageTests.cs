// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    [Parallelizable(ParallelScope.All)]
    public class BloomStorageTests
    {
        [Timeout(Timeout.MaxTestTime)]
        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(0, 10)]
        [TestCase(10, 12)]
        public void Empty_storage_does_not_contain_blocks(long from, long to)
        {
            BloomStorage storage = new(new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());
            storage.ContainsRange(from, to).Should().BeFalse();
        }

        [Timeout(Timeout.MaxTestTime)]
        [TestCase(0, 0, ExpectedResult = false)]
        [TestCase(1, 1, ExpectedResult = true)]
        [TestCase(0, 10, ExpectedResult = false)]
        [TestCase(1, 10, ExpectedResult = true)]
        [TestCase(10, 12, ExpectedResult = false)]
        public bool Initialized_storage_contain_blocks_as_db(long from, long to)
        {
            MemDb memColumnsDb = new();
            memColumnsDb.Set(BloomStorage.MinBlockNumberKey, 1L.ToBigEndianByteArrayWithoutLeadingZeros());
            memColumnsDb.Set(BloomStorage.MaxBlockNumberKey, 11L.ToBigEndianByteArrayWithoutLeadingZeros());
            BloomStorage storage = new(new BloomConfig(), memColumnsDb, new InMemoryDictionaryFileStoreFactory());
            return storage.ContainsRange(from, to);
        }

        [Timeout(Timeout.MaxTestTime)]
        [TestCase(0, 0, ExpectedResult = false)]
        [TestCase(1, 1, ExpectedResult = true)]
        [TestCase(0, 10, ExpectedResult = false)]
        [TestCase(1, 10, ExpectedResult = true)]
        [TestCase(10, 12, ExpectedResult = false)]
        public bool Contain_blocks_after_store(long from, long to)
        {
            BloomStorage storage = new(new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());

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
                IEnumerable<long> GetRange(long expectedFound, int offset = 0) => Enumerable.Range(offset, (int)expectedFound).Select(i => (long)i);
                int searchesPerBucket = 1 + LevelMultiplier + LevelMultiplier * LevelMultiplier + LevelMultiplier * LevelMultiplier * LevelMultiplier;

                int bucketItems = new BloomStorage(new BloomConfig() { IndexLevelBucketSizes = new[] { LevelMultiplier, LevelMultiplier, LevelMultiplier } }, new MemDb(), new InMemoryDictionaryFileStoreFactory()).MaxBucketSize;
                int count = bucketItems * Buckets;
                int maxIndex = count - 1;
                yield return new TestCaseData(0, maxIndex, false, Enumerable.Empty<long>(), Buckets);
                yield return new TestCaseData(0, maxIndex, true, GetRange(count), Buckets * searchesPerBucket);
                yield return new TestCaseData(5, 49, true, GetRange(45, 5), 4 + 45); // 4 lookups at level one (16), 45 lookups at bottom level (49-5+1)
                yield return new TestCaseData(0, LevelMultiplier * LevelMultiplier * LevelMultiplier - 1, true, GetRange(LevelMultiplier * LevelMultiplier * LevelMultiplier), searchesPerBucket - 1); // skips highest level
                yield return new TestCaseData(0, LevelMultiplier * LevelMultiplier * LevelMultiplier * 2 - 1, true, GetRange(LevelMultiplier * LevelMultiplier * LevelMultiplier * 2), (searchesPerBucket - 1) * 2); // skips highest level
                yield return new TestCaseData(0, LevelMultiplier * LevelMultiplier * LevelMultiplier * 3 - 1, true, GetRange(LevelMultiplier * LevelMultiplier * LevelMultiplier * 3), searchesPerBucket * 3); // doesn't skip highest level
            }
        }

        [TestCaseSource(nameof(GetBloomsTestCases))]
        public void Returns_proper_blooms_after_store(long from, long to, bool isMatch, IEnumerable<long> expectedBlocks, long expectedBloomsChecked)
        {
            BloomStorage storage = CreateBloomStorage(new BloomConfig() { IndexLevelBucketSizes = new[] { LevelMultiplier, LevelMultiplier, LevelMultiplier } });
            long bloomsChecked = 0;

            IBloomEnumeration bloomEnumeration = storage.GetBlooms(from, to);
            IList<long> ranges = new List<long>();
            foreach (Core.Bloom unused in bloomEnumeration)
            {
                bloomsChecked++;
                if (isMatch && bloomEnumeration.TryGetBlockNumber(out long blockNumber))
                {
                    ranges.Add(blockNumber);
                }
            }

            ranges.Should().BeEquivalentTo(expectedBlocks);
            bloomsChecked.Should().Be(expectedBloomsChecked);
        }

        [Timeout(Timeout.MaxTestTime)]
        [TestCase(1, 10, new long[] { 4 }, new[] { 4 })]
        [TestCase(0, 4, new long[] { 4 }, new[] { 4 })]
        [TestCase(1, 10, new long[] { 1, 4, 6, 8 }, new[] { 4 })]
        [TestCase(1, 10, new long[] { 4, 6, 8 }, new[] { 4, 4 })]
        [TestCase(1, 10, new long[] { 4, 8, 16, 32 }, new[] { 4, 4 })]
        [TestCase(1, 48, new long[] { 4, 8, 16, 32 }, new[] { 4, 4 })]
        [TestCase(5, 60, new long[] { 4, 8, 49 }, new[] { 8, 3 })]
        [TestCase(1, 120, new long[] { 4, 8, 64, 65 }, new[] { 4, 4, 4 })]
        [TestCase(0, 120, new long[] { 0, 1, 2, 3, 5, 7, 11, 120 }, new[] { 9, 3 })]
        public void Can_find_bloom_with_fromBlock_offset(long from, long to, long[] blocksSet, int[] levels)
        {
            BloomStorage storage = CreateBloomStorage(new BloomConfig { IndexLevelBucketSizes = levels });
            Core.Bloom bloom = new();
            byte[] bytes = { 1, 2, 3 };
            bloom.Set(bytes);
            foreach (long blockNumber in blocksSet)
            {
                if (blockNumber > storage.MaxBlockNumber + 1)
                {
                    // Assert.Fail($"Missing blocks. Trying inserting {blockNumber}, when current max block is {storage.MaxBlockNumber}.");
                }
                storage.Store(blockNumber, bloom);
            }

            IBloomEnumeration bloomEnumeration = storage.GetBlooms(from, to);
            IList<long> foundBlocks = new List<long>(blocksSet.Length);
            foreach (Core.Bloom b in bloomEnumeration)
            {
                if (b.Matches(bytes) && bloomEnumeration.TryGetBlockNumber(out long block))
                {
                    foundBlocks.Add(block);
                }
            }

            long[] expectedFoundBlocks = blocksSet.Where(b => b >= from && b <= to).ToArray();
            TestContext.Out.WriteLine($"Expected found blocks: {string.Join(", ", expectedFoundBlocks)}");
            foundBlocks.Should().BeEquivalentTo(expectedFoundBlocks);
        }

        private const int Buckets = 3;
        private const int LevelMultiplier = 16;

        private static BloomStorage CreateBloomStorage(BloomConfig? bloomConfig = null)
        {
            BloomStorage storage = new(bloomConfig ?? new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());
            int bucketItems = storage.MaxBucketSize * Buckets;

            for (long i = 0; i < bucketItems; i++)
            {
                storage.Store(i, Core.Bloom.Empty);
            }

            return storage;
        }

        [Timeout(Timeout.MaxTestTime)]
        [TestCase(byte.MaxValue)]
        [TestCase(ushort.MaxValue / 4)]
        [TestCase(ushort.MaxValue, Explicit = true)]
        [TestCase(ushort.MaxValue * 8 + 7, Explicit = true)]
        [TestCase(ushort.MaxValue * 128 + 127, Explicit = true)]
        public void Can_safely_insert_concurrently(int maxBlock)
        {
            BloomConfig config = new() { IndexLevelBucketSizes = new[] { 16, 16, 16 } };
            string basePath = Path.Combine(Path.GetTempPath(), DbNames.Bloom, maxBlock.ToString());
            try
            {
                FixedSizeFileStoreFactory fileStorageFactory = new(basePath, DbNames.Bloom, Core.Bloom.ByteLength);
                using BloomStorage storage = new(config, new MemDb(), fileStorageFactory);

                Parallel.For(0, maxBlock + 1,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 16 },
                    i =>
                    {
                        Core.Bloom bloom = new();
                        bloom.Set(i % Core.Bloom.BitLength);
                        storage.Store(i, bloom);
                    });

                IBloomEnumeration blooms = storage.GetBlooms(0, maxBlock);
                int j = 0;
                foreach (Core.Bloom bloom in blooms)
                {
                    j++;
                    (long FromBlock, long ToBlock) currentIndices = blooms.CurrentIndices;
                    int fromBlock = (int)(currentIndices.FromBlock % Core.Bloom.BitLength);
                    int toBlock = (int)(Math.Min(currentIndices.ToBlock, maxBlock) % Core.Bloom.BitLength);
                    Core.Bloom expectedBloom = new();
                    for (int i = fromBlock; i <= toBlock; i++)
                    {
                        expectedBloom.Set(i);
                    }

                    bloom.Should().Be(expectedBloom, $"blocks <{currentIndices.FromBlock}, {currentIndices.ToBlock}>");
                    blooms.TryGetBlockNumber(out _);
                }

                TestContext.WriteLine($"Checked {j} blooms");
            }
            finally
            {
                Directory.Delete(basePath, true);
            }
        }

        private IEnumerable<(Core.Bloom Bloom, (long FromBlock, long ToBlock) CurrentIndices)> Unwind(IBloomEnumeration blooms)
        {
            foreach (Core.Bloom bloom in blooms)
            {
                (long FromBlock, long ToBlock) currentIndices = blooms.CurrentIndices;
                yield return (bloom, currentIndices);
                blooms.TryGetBlockNumber(out _);
            }
        }
    }
}
