// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Bloom;

[Parallelizable(ParallelScope.All)]
public class BloomStorageTests
{
    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(0ul, 0ul)]
    [TestCase(1ul, 1ul)]
    [TestCase(0ul, 10ul)]
    [TestCase(10ul, 12ul)]
    public void Empty_storage_does_not_contain_blocks(ulong from, ulong to)
    {
        BloomStorage storage = new(new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());
        Assert.That(storage.ContainsRange(from, to), Is.False);
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(0ul, 0ul, ExpectedResult = false)]
    [TestCase(1ul, 1ul, ExpectedResult = true)]
    [TestCase(0ul, 10ul, ExpectedResult = false)]
    [TestCase(1ul, 10ul, ExpectedResult = true)]
    [TestCase(10ul, 12ul, ExpectedResult = false)]
    public bool Initialized_storage_contain_blocks_as_db(ulong from, ulong to)
    {
        MemDb memColumnsDb = new();
        memColumnsDb.Set(BloomStorage.MinBlockNumberKey, 1UL.ToBigEndianByteArrayWithoutLeadingZeros());
        memColumnsDb.Set(BloomStorage.MaxBlockNumberKey, 11UL.ToBigEndianByteArrayWithoutLeadingZeros());
        BloomStorage storage = new(new BloomConfig(), memColumnsDb, new InMemoryDictionaryFileStoreFactory());
        return storage.ContainsRange(from, to);
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(0ul, 0ul, ExpectedResult = false)]
    [TestCase(1ul, 1ul, ExpectedResult = true)]
    [TestCase(0ul, 10ul, ExpectedResult = false)]
    [TestCase(1ul, 10ul, ExpectedResult = true)]
    [TestCase(10ul, 12ul, ExpectedResult = false)]
    public bool Contain_blocks_after_store(ulong from, ulong to)
    {
        BloomStorage storage = new(new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());

        for (ulong i = 1; i < 11; i++)
        {
            storage.Store(i, Core.Bloom.Empty);
        }

        return storage.ContainsRange(from, to);
    }

    public static IEnumerable GetBloomsTestCases
    {
        get
        {
            static IEnumerable<ulong> GetRange(long expectedFound, int offset = 0) => Enumerable.Range(offset, (int)expectedFound).Select(static i => (ulong)i);
            int searchesPerBucket = 1 + LevelMultiplier + LevelMultiplier * LevelMultiplier + LevelMultiplier * LevelMultiplier * LevelMultiplier;

            int bucketItems = (int)new BloomStorage(new BloomConfig() { IndexLevelBucketSizes = new[] { LevelMultiplier, LevelMultiplier, LevelMultiplier } }, new MemDb(), new InMemoryDictionaryFileStoreFactory()).MaxBucketSize;
            int count = bucketItems * (int)Buckets;
            int maxIndex = count - 1;
            yield return new TestCaseData(0UL, (ulong)maxIndex, false, Enumerable.Empty<ulong>(), Buckets)
                .SetName("Returns_no_blocks_when_blooms_do_not_match");
            yield return new TestCaseData(0UL, (ulong)maxIndex, true, GetRange(count), Buckets * searchesPerBucket)
                .SetName("Returns_all_blocks_when_all_blooms_match");
            yield return new TestCaseData(5UL, 49UL, true, GetRange(45, 5), 4 + 45)
                .SetName("Returns_range_after_level_one_lookup");
            yield return new TestCaseData(0UL, (ulong)(LevelMultiplier * LevelMultiplier * LevelMultiplier - 1), true, GetRange(LevelMultiplier * LevelMultiplier * LevelMultiplier), searchesPerBucket - 1)
                .SetName("Returns_single_bucket_without_highest_level_lookup");
            yield return new TestCaseData(0UL, (ulong)(LevelMultiplier * LevelMultiplier * LevelMultiplier * 2 - 1), true, GetRange(LevelMultiplier * LevelMultiplier * LevelMultiplier * 2), (searchesPerBucket - 1) * 2)
                .SetName("Returns_two_buckets_without_highest_level_lookup");
            yield return new TestCaseData(0UL, (ulong)(LevelMultiplier * LevelMultiplier * LevelMultiplier * 3 - 1), true, GetRange(LevelMultiplier * LevelMultiplier * LevelMultiplier * 3), searchesPerBucket * 3)
                .SetName("Returns_three_buckets_with_highest_level_lookup");
        }
    }

    [TestCaseSource(nameof(GetBloomsTestCases))]
    public void Returns_proper_blooms_after_store(ulong from, ulong to, bool isMatch, IEnumerable<ulong> expectedBlocks, long expectedBloomsChecked)
    {
        BloomStorage storage = CreateBloomStorage(new BloomConfig() { IndexLevelBucketSizes = new[] { LevelMultiplier, LevelMultiplier, LevelMultiplier } });
        long bloomsChecked = 0;

        IBloomEnumeration bloomEnumeration = storage.GetBlooms(from, to);
        IList<ulong> ranges = [];
        foreach (Core.Bloom unused in bloomEnumeration)
        {
            bloomsChecked++;
            if (isMatch && bloomEnumeration.TryGetBlockNumber(out ulong blockNumber))
            {
                ranges.Add(blockNumber);
            }
        }

        Assert.That(ranges, Is.EqualTo(expectedBlocks));
        Assert.That(bloomsChecked, Is.EqualTo(expectedBloomsChecked));
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(1UL, 10UL, new ulong[] { 4 }, new[] { 4 })]
    [TestCase(0UL, 4UL, new ulong[] { 4 }, new[] { 4 })]
    [TestCase(1UL, 10UL, new ulong[] { 1, 4, 6, 8 }, new[] { 4 })]
    [TestCase(1UL, 10UL, new ulong[] { 4, 6, 8 }, new[] { 4, 4 })]
    [TestCase(1UL, 10UL, new ulong[] { 4, 8, 16, 32 }, new[] { 4, 4 })]
    [TestCase(1UL, 48UL, new ulong[] { 4, 8, 16, 32 }, new[] { 4, 4 })]
    [TestCase(5UL, 60UL, new ulong[] { 4, 8, 49 }, new[] { 8, 3 })]
    [TestCase(1UL, 120UL, new ulong[] { 4, 8, 64, 65 }, new[] { 4, 4, 4 })]
    [TestCase(0UL, 120UL, new ulong[] { 0, 1, 2, 3, 5, 7, 11, 120 }, new[] { 9, 3 })]
    public void Can_find_bloom_with_fromBlock_offset(ulong from, ulong to, ulong[] blocksSet, int[] levels)
    {
        BloomStorage storage = CreateBloomStorage(new BloomConfig { IndexLevelBucketSizes = levels });
        Core.Bloom bloom = new();
        byte[] bytes = { 1, 2, 3 };
        bloom.Set(bytes);
        foreach (ulong blockNumber in blocksSet)
        {
            if (blockNumber > storage.MaxBlockNumber + 1)
            {
                // Assert.Fail($"Missing blocks. Trying inserting {blockNumber}, when current max block is {storage.MaxBlockNumber}.");
            }
            storage.Store(blockNumber, bloom);
        }

        IBloomEnumeration bloomEnumeration = storage.GetBlooms(from, to);
        IList<ulong> foundBlocks = new List<ulong>(blocksSet.Length);
        foreach (Core.Bloom b in bloomEnumeration)
        {
            if (b.Matches(bytes) && bloomEnumeration.TryGetBlockNumber(out ulong block))
            {
                foundBlocks.Add(block);
            }
        }

        ulong[] expectedFoundBlocks = blocksSet.Where(b => b >= from && b <= to).ToArray();
        TestContext.Out.WriteLine($"Expected found blocks: {string.Join(", ", expectedFoundBlocks)}");
        Assert.That(foundBlocks, Is.EqualTo(expectedFoundBlocks));
    }

    private const uint Buckets = 3;
    private const int LevelMultiplier = 16;

    private static BloomStorage CreateBloomStorage(BloomConfig? bloomConfig = null)
    {
        BloomStorage storage = new(bloomConfig ?? new BloomConfig(), new MemDb(), new InMemoryDictionaryFileStoreFactory());
        ulong bucketItems = storage.MaxBucketSize * Buckets;

        for (ulong i = 0; i < bucketItems; i++)
        {
            storage.Store(i, Core.Bloom.Empty);
        }

        return storage;
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(byte.MaxValue)]
    [TestCase(ushort.MaxValue / 4u)]
    [TestCase(ushort.MaxValue, Explicit = true)]
    [TestCase(ushort.MaxValue * 8u + 7u, Explicit = true)]
    [TestCase(ushort.MaxValue * 128u + 127u, Explicit = true)]
    public void Can_safely_insert_concurrently(uint maxBlock) => RunInsertAndVerify(maxBlock, (storage, count) =>
    {
        Parallel.For(0, count,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 16 },
            i =>
            {
                Core.Bloom bloom = new();
                bloom.Set(i % Core.Bloom.BitLength);
                storage.Store((ulong)i, bloom);
            });
    });

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(byte.MaxValue)]
    [TestCase(ushort.MaxValue / 4u)]
    [TestCase(ushort.MaxValue, Explicit = true)]
    [TestCase(ushort.MaxValue * 8u + 7u, Explicit = true)]
    [TestCase(ushort.MaxValue * 128u + 127u, Explicit = true)]
    public void Can_safely_insert_in_batch(uint maxBlock) => RunInsertAndVerify(maxBlock, (storage, count) =>
    {
        using ArrayPoolList<(ulong, Core.Bloom)> bloomInsertions = new(count);
        for (int i = 0; i < count; i++)
        {
            Core.Bloom bloom = new();
            bloom.Set(i % Core.Bloom.BitLength);
            bloomInsertions.Add(((ulong)i, bloom));
        }
        storage.Store(bloomInsertions);
    });

    private static void RunInsertAndVerify(uint maxBlock, Action<BloomStorage, int> insertAction)
    {
        BloomConfig config = new() { IndexLevelBucketSizes = new[] { 16, 16, 16 } };
        TempPath tempPath = TempPath.GetTempDirectory();
        string basePath = tempPath.Path;
        try
        {
            FixedSizeFileStoreFactory fileStorageFactory = new(basePath, DbNames.Bloom, Core.Bloom.ByteLength);
            using BloomStorage storage = new(config, new MemDb(), fileStorageFactory);

            insertAction(storage, (int)maxBlock + 1);

            IBloomEnumeration blooms = storage.GetBlooms(0, maxBlock);
            int j = 0;
            foreach (Core.Bloom bloom in blooms)
            {
                j++;
                (ulong FromBlock, ulong ToBlock) = blooms.CurrentIndices;
                int fromBlock = (int)(FromBlock % Core.Bloom.BitLength);
                int toBlock = (int)(Math.Min(ToBlock, maxBlock) % Core.Bloom.BitLength);
                Core.Bloom expectedBloom = new();
                for (int i = fromBlock; i <= toBlock; i++)
                {
                    expectedBloom.Set(i);
                }

                Assert.That(bloom, Is.EqualTo(expectedBloom), $"blocks <{FromBlock}, {ToBlock}>");
                blooms.TryGetBlockNumber(out _);
            }

            TestContext.Out.WriteLine($"Checked {j} blooms");
        }
        finally
        {
            Directory.Delete(basePath, true);
        }
    }
}
