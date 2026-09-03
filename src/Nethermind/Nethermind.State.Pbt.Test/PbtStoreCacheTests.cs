// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtStoreCacheTests
{
    [TestCase(false, PbtPartition.Account)]
    [TestCase(false, PbtPartition.Code)]
    [TestCase(false, PbtPartition.Storage)]
    [TestCase(true, PbtPartition.Account)]
    [TestCase(true, PbtPartition.Code)]
    [TestCase(true, PbtPartition.Storage)]
    public void EntriesRequireBothLogicalKeyAndHash(bool trieNode, PbtPartition partition)
    {
        using PbtStoreCache cache = new(ConfigWithBudget(trieNode, partition, 256));
        Stem key = StemFor(partition, 1);
        Stem other = StemFor(partition, 2);
        ValueHash256 hash = Hash(3);
        ValueHash256 otherHash = Hash(4);
        using RefCountingMemory value = Memory(5);

        if (trieNode) cache.SetTrieNode(TrieNodeKey.For(16, key), hash, value);
        else cache.SetLeafBlob(key, hash, value);

        using RefCountingMemory? hit = trieNode
            ? cache.GetTrieNode(TrieNodeKey.For(16, key), hash)
            : cache.GetLeafBlob(key, hash);
        using RefCountingMemory? wrongHash = trieNode
            ? cache.GetTrieNode(TrieNodeKey.For(16, key), otherHash)
            : cache.GetLeafBlob(key, otherHash);
        using RefCountingMemory? wrongKey = trieNode
            ? cache.GetTrieNode(TrieNodeKey.For(16, other), hash)
            : cache.GetLeafBlob(other, hash);

        Assert.That(hit?.GetSpan().ToArray(), Is.EqualTo(new byte[] { 5 }));
        Assert.That(wrongHash, Is.Null);
        Assert.That(wrongKey, Is.Null);
    }

    [TestCase(false, PbtPartition.Account)]
    [TestCase(false, PbtPartition.Code)]
    [TestCase(false, PbtPartition.Storage)]
    [TestCase(true, PbtPartition.Account)]
    [TestCase(true, PbtPartition.Code)]
    [TestCase(true, PbtPartition.Storage)]
    public void MetricsTrackExactBucketDeltas(bool trieNode, PbtPartition partition)
    {
        PbtSnapshotMemoryLabel label = StoreCacheLabel(trieNode, partition);
        long memoryBaseline = Metrics.PbtStoreCacheMemory[label];
        long hitBaseline = Metrics.PbtStoreCacheHits[label];
        long missBaseline = Metrics.PbtStoreCacheMisses[label];
        using PbtStoreCache cache = new(ConfigWithBudget(trieNode, partition, 1280));
        Stem stem = StemFor(partition, 1);
        ValueHash256 hash = Hash(1);
        ValueHash256 otherHash = Hash(2);
        using RefCountingMemory first = Memory(4, 5, 6);
        using RefCountingMemory replacement = Memory(7, 8, 9, 10, 11);

        using RefCountingMemory? miss = Get(cache, trieNode, stem, hash);
        AssertMetrics(label, memoryBaseline, hitBaseline, missBaseline + 1);

        Set(cache, trieNode, stem, hash, first);
        AssertMetrics(label, memoryBaseline + 3, hitBaseline, missBaseline + 1);

        using RefCountingMemory? hit = Get(cache, trieNode, stem, hash);
        AssertMetrics(label, memoryBaseline + 3, hitBaseline + 1, missBaseline + 1);

        using RefCountingMemory? wrongHash = Get(cache, trieNode, stem, otherHash);
        Assert.That(wrongHash, Is.Null);
        AssertMetrics(label, memoryBaseline + 3, hitBaseline + 1, missBaseline + 2);

        Set(cache, trieNode, stem, hash, replacement);
        AssertMetrics(label, memoryBaseline + 5, hitBaseline + 1, missBaseline + 2);

        cache.Dispose();
        AssertMetrics(label, memoryBaseline, hitBaseline + 1, missBaseline + 2);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MemoryMetricTracksPruning(bool trieNode)
    {
        PbtSnapshotMemoryLabel label = StoreCacheLabel(trieNode, PbtPartition.Account);
        long memoryBaseline = Metrics.PbtStoreCacheMemory[label];
        using PbtStoreCache cache = new(ConfigWithBudget(trieNode, PbtPartition.Account, 256));
        Stem firstStem = StemInShard(PbtPartition.Account, 1, 1);
        Stem secondStem = StemInShard(PbtPartition.Account, 1, 2);
        using RefCountingMemory first = Memory(1);
        using RefCountingMemory second = Memory(2);

        Set(cache, trieNode, firstStem, Hash(1), first);
        Assert.That(Metrics.PbtStoreCacheMemory[label], Is.EqualTo(memoryBaseline + 1));

        Set(cache, trieNode, secondStem, Hash(2), second);
        Assert.That(Metrics.PbtStoreCacheMemory[label], Is.EqualTo(memoryBaseline + 1));

        cache.Dispose();
        Assert.That(Metrics.PbtStoreCacheMemory[label], Is.EqualTo(memoryBaseline));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ZeroBudgetDisablesOnlyItsBucket(bool trieNode)
    {
        PbtConfig config = EmptyConfig();
        config.AccountLeafBlobCacheSizeBudget = trieNode ? 256UL : 0;
        config.AccountTrieNodeCacheSizeBudget = trieNode ? 0 : 256UL;
        using PbtStoreCache cache = new(config);
        Stem stem = StemFor(PbtPartition.Account, 1);
        TrieNodeKey key = TrieNodeKey.For(PbtPartitions.RootDepth(PbtPartition.Account), stem);
        ValueHash256 hash = Hash(2);
        using RefCountingMemory value = Memory(3);

        if (trieNode) cache.SetTrieNode(key, hash, value);
        else cache.SetLeafBlob(stem, hash, value);

        using RefCountingMemory? miss = trieNode ? cache.GetTrieNode(key, hash) : cache.GetLeafBlob(stem, hash);
        Assert.That(miss, Is.Null);
    }

    [TestCase(false, PbtPartition.Account)]
    [TestCase(false, PbtPartition.Code)]
    [TestCase(false, PbtPartition.Storage)]
    [TestCase(true, PbtPartition.Account)]
    [TestCase(true, PbtPartition.Code)]
    [TestCase(true, PbtPartition.Storage)]
    public void EvictionReplacementAndDisposalReleaseCacheLeases(bool trieNode, PbtPartition partition)
    {
        PbtConfig config = ConfigWithBudget(trieNode, partition, 256);
        PbtStoreCache cache = new(config);
        Stem firstKey = StemInShard(partition, 1, 1);
        Stem secondKey = StemInShard(partition, 1, 2);
        ValueHash256 firstHash = Hash(1);
        ValueHash256 secondHash = Hash(2);
        RefCountingMemory evicted = Memory(1);
        RefCountingMemory replaced = Memory(2);
        RefCountingMemory retained = Memory(3);

        Set(cache, trieNode, firstKey, firstHash, evicted);
        ((IDisposable)evicted).Dispose();
        Set(cache, trieNode, secondKey, firstHash, replaced);
        ((IDisposable)replaced).Dispose();
        Set(cache, trieNode, secondKey, secondHash, retained);
        ((IDisposable)retained).Dispose();

        using RefCountingMemory? evictedMiss = Get(cache, trieNode, firstKey, firstHash);
        using RefCountingMemory? replacedHashMiss = Get(cache, trieNode, secondKey, firstHash);
        RefCountingMemory? replacement = Get(cache, trieNode, secondKey, secondHash);
        Assert.That(replacement?.GetSpan().ToArray(), Is.EqualTo(new byte[] { 3 }));
        ((IDisposable?)replacement)?.Dispose();
        cache.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(evictedMiss, Is.Null);
            Assert.That(replacedHashMiss, Is.Null);
            Assert.That(TrackingMemoryProvider.CountUnreleased([evicted]), Is.Zero, "eviction releases the cache lease");
            Assert.That(TrackingMemoryProvider.CountUnreleased([replaced]), Is.Zero, "replacement releases the old cache lease");
            Assert.That(TrackingMemoryProvider.CountUnreleased([retained]), Is.Zero, "disposal releases the retained cache lease");
        }
    }

    [TestCase(false, PbtPartition.Account)]
    [TestCase(false, PbtPartition.Code)]
    [TestCase(false, PbtPartition.Storage)]
    [TestCase(true, PbtPartition.Account)]
    [TestCase(true, PbtPartition.Code)]
    [TestCase(true, PbtPartition.Storage)]
    public void KeysDifferingBelowThePartitionPrefixShardApart(bool trieNode, PbtPartition partition)
    {
        PbtStoreCache cache = new(ConfigWithBudget(trieNode, partition, 256));
        Stem firstShard = StemBelowRoot(partition, 0);
        Stem secondShard = StemBelowRoot(partition, 1);
        ValueHash256 hash = Hash(1);
        RefCountingMemory evicted = Memory(1);
        RefCountingMemory retained = Memory(2);

        Set(cache, trieNode, firstShard, hash, evicted);
        ((IDisposable)evicted).Dispose();
        Set(cache, trieNode, secondShard, hash, retained);
        ((IDisposable)retained).Dispose();

        RefCountingMemory? first = Get(cache, trieNode, firstShard, hash);
        RefCountingMemory? second = Get(cache, trieNode, secondShard, hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first?.GetSpan().ToArray(), Is.EqualTo(new byte[] { 1 }));
            Assert.That(second?.GetSpan().ToArray(), Is.EqualTo(new byte[] { 2 }));
        }
        ((IDisposable?)first)?.Dispose();
        ((IDisposable?)second)?.Dispose();
        cache.Dispose();

        Assert.That(TrackingMemoryProvider.CountUnreleased([evicted, retained]), Is.Zero);
    }

    [TestCase(false, PbtPartition.Account)]
    [TestCase(false, PbtPartition.Code)]
    [TestCase(false, PbtPartition.Storage)]
    [TestCase(true, PbtPartition.Account)]
    [TestCase(true, PbtPartition.Code)]
    [TestCase(true, PbtPartition.Storage)]
    public void ConcurrentAccessBalancesEveryLease(bool trieNode, PbtPartition partition)
    {
        const int operationCount = 128;
        PbtSnapshotMemoryLabel label = StoreCacheLabel(trieNode, partition);
        long memoryBaseline = Metrics.PbtStoreCacheMemory[label];
        long hitBaseline = Metrics.PbtStoreCacheHits[label];
        long missBaseline = Metrics.PbtStoreCacheMisses[label];
        using PbtStoreCache cache = new(ConfigWithBudget(trieNode, partition, 4096));
        Stem stem = StemFor(partition, 1);
        ValueHash256 hash = Hash(1);
        List<RefCountingMemory> values = new(operationCount);
        object valuesLock = new();

        Parallel.For(0, operationCount, marker =>
        {
            RefCountingMemory value = Memory((byte)marker);
            lock (valuesLock) values.Add(value);
            Set(cache, trieNode, stem, hash, value);
            ((IDisposable)value).Dispose();
            using RefCountingMemory? read = Get(cache, trieNode, stem, hash);
        });

        cache.Dispose();
        long recordedLookups = Metrics.PbtStoreCacheHits[label] - hitBaseline + Metrics.PbtStoreCacheMisses[label] - missBaseline;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TrackingMemoryProvider.CountUnreleased(values), Is.Zero);
            Assert.That(Metrics.PbtStoreCacheMemory[label], Is.EqualTo(memoryBaseline));
            Assert.That(recordedLookups, Is.EqualTo(operationCount));
        }
    }

    [Test]
    public void ExhaustingOneBucketDoesNotEvictAnother()
    {
        PbtConfig config = EmptyConfig();
        config.AccountLeafBlobCacheSizeBudget = 256;
        config.CodeLeafBlobCacheSizeBudget = 256;
        using PbtStoreCache cache = new(config);
        Stem accountA = StemInShard(PbtPartition.Account, 1, 1);
        Stem accountB = StemInShard(PbtPartition.Account, 1, 2);
        Stem code = StemFor(PbtPartition.Code, 1);
        ValueHash256 hash = Hash(1);
        using RefCountingMemory valueA = Memory(1);
        using RefCountingMemory valueB = Memory(2);
        using RefCountingMemory codeValue = Memory(3);

        cache.SetLeafBlob(accountA, hash, valueA);
        cache.SetLeafBlob(code, hash, codeValue);
        cache.SetLeafBlob(accountB, hash, valueB);

        using RefCountingMemory? evicted = cache.GetLeafBlob(accountA, hash);
        using RefCountingMemory? otherBucket = cache.GetLeafBlob(code, hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(evicted, Is.Null);
            Assert.That(otherBucket?.GetSpan().ToArray(), Is.EqualTo(new byte[] { 3 }));
        }
    }

    [Test]
    public void LeafClockGivesRecentlyReadEntryASecondChance()
    {
        PbtConfig config = ConfigWithBudget(false, PbtPartition.Account, 3 * 256);
        using PbtStoreCache cache = new(config);
        Stem[] stems = StemsInDistinctCacheSlots(false, 5);
        ValueHash256 hash = Hash(1);
        using RefCountingMemory first = Memory(1);
        using RefCountingMemory second = Memory(2);
        using RefCountingMemory third = Memory(3);
        using RefCountingMemory fourth = Memory(4);
        using RefCountingMemory fifth = Memory(5);

        cache.SetLeafBlob(stems[0], hash, first);
        cache.SetLeafBlob(stems[1], hash, second);
        cache.SetLeafBlob(stems[2], hash, third);
        cache.SetLeafBlob(stems[3], hash, fourth);
        using (RefCountingMemory? recentlyRead = cache.GetLeafBlob(stems[1], hash)) { }
        cache.SetLeafBlob(stems[4], hash, fifth);

        using RefCountingMemory? firstMiss = cache.GetLeafBlob(stems[0], hash);
        using RefCountingMemory? secondHit = cache.GetLeafBlob(stems[1], hash);
        using RefCountingMemory? thirdMiss = cache.GetLeafBlob(stems[2], hash);
        using RefCountingMemory? fourthHit = cache.GetLeafBlob(stems[3], hash);
        using RefCountingMemory? fifthHit = cache.GetLeafBlob(stems[4], hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstMiss, Is.Null);
            Assert.That(secondHit, Is.Not.Null);
            Assert.That(thirdMiss, Is.Null);
            Assert.That(fourthHit, Is.Not.Null);
            Assert.That(fifthHit, Is.Not.Null);
        }
    }

    [Test]
    public void LeafClockEvictsByExactPayloadBytes()
    {
        PbtConfig config = ConfigWithBudget(false, PbtPartition.Account, 5 * 256);
        using PbtStoreCache cache = new(config);
        Stem[] stems = StemsInDistinctCacheSlots(false, 3);
        ValueHash256 hash = Hash(1);
        using RefCountingMemory large = SizedMemory(4, 1);
        using RefCountingMemory small = SizedMemory(1, 2);
        using RefCountingMemory replacement = SizedMemory(2, 3);

        cache.SetLeafBlob(stems[0], hash, large);
        cache.SetLeafBlob(stems[1], hash, small);
        cache.SetLeafBlob(stems[2], hash, replacement);

        using RefCountingMemory? largeMiss = cache.GetLeafBlob(stems[0], hash);
        using RefCountingMemory? smallHit = cache.GetLeafBlob(stems[1], hash);
        using RefCountingMemory? replacementHit = cache.GetLeafBlob(stems[2], hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(largeMiss, Is.Null);
            Assert.That(smallHit, Is.Not.Null);
            Assert.That(replacementHit, Is.Not.Null);
        }
    }

    [Test]
    public void TrieOverflowRefreshesWholeShard()
    {
        PbtConfig config = ConfigWithBudget(true, PbtPartition.Account, 2 * 256);
        using PbtStoreCache cache = new(config);
        Stem[] stems = StemsInDistinctCacheSlots(true, 3);
        ValueHash256 hash = Hash(1);
        using RefCountingMemory first = Memory(1);
        using RefCountingMemory second = Memory(2);
        using RefCountingMemory replacement = Memory(3);

        Set(cache, true, stems[0], hash, first);
        Set(cache, true, stems[1], hash, second);
        Set(cache, true, stems[2], hash, replacement);

        using RefCountingMemory? firstMiss = Get(cache, true, stems[0], hash);
        using RefCountingMemory? secondMiss = Get(cache, true, stems[1], hash);
        using RefCountingMemory? replacementHit = Get(cache, true, stems[2], hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstMiss, Is.Null);
            Assert.That(secondMiss, Is.Null);
            Assert.That(replacementHit, Is.Not.Null);
        }
    }

    [Test]
    public void ShardBudgetsSplitExactRemainderAndRejectOversizedValues()
    {
        PbtConfig config = ConfigWithBudget(false, PbtPartition.Account, 257);
        using PbtStoreCache cache = new(config);
        Stem shardZero = StemBelowRoot(PbtPartition.Account, 0);
        Stem shardOne = StemBelowRoot(PbtPartition.Account, 1);
        ValueHash256 hash = Hash(1);
        using RefCountingMemory twoBytes = SizedMemory(2, 1);
        using RefCountingMemory oversized = SizedMemory(2, 2);
        using RefCountingMemory oneByte = Memory(3);

        cache.SetLeafBlob(shardZero, hash, twoBytes);
        cache.SetLeafBlob(shardOne, hash, oversized);
        using RefCountingMemory? shardZeroHit = cache.GetLeafBlob(shardZero, hash);
        using RefCountingMemory? oversizedMiss = cache.GetLeafBlob(shardOne, hash);
        cache.SetLeafBlob(shardOne, hash, oneByte);
        using RefCountingMemory? shardOneHit = cache.GetLeafBlob(shardOne, hash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(shardZeroHit?.GetSpan().Length, Is.EqualTo(2));
            Assert.That(oversizedMiss, Is.Null);
            Assert.That(shardOneHit?.GetSpan().Length, Is.EqualTo(1));
        }
    }

    [Test]
    public void StorageLeafCapacityUsesFortyByteEstimateAndExactPayloadLimit()
    {
        Assert.That(PbtStoreCache.EstimateLeafBlobSize(PbtPartition.Storage), Is.EqualTo(40));

        PbtConfig config = ConfigWithBudget(false, PbtPartition.Storage, 40 * 256);
        using PbtStoreCache cache = new(config);
        Stem acceptedStem = StemInShard(PbtPartition.Storage, 1, 1);
        Stem rejectedStem = StemInShard(PbtPartition.Storage, 2, 2);
        ValueHash256 hash = Hash(1);
        using RefCountingMemory accepted = SizedMemory(40, 1);
        using RefCountingMemory rejected = SizedMemory(41, 2);

        cache.SetLeafBlob(acceptedStem, hash, accepted);
        cache.SetLeafBlob(rejectedStem, hash, rejected);

        using RefCountingMemory? acceptedHit = cache.GetLeafBlob(acceptedStem, hash);
        using RefCountingMemory? rejectedMiss = cache.GetLeafBlob(rejectedStem, hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(acceptedHit?.GetSpan().Length, Is.EqualTo(40));
            Assert.That(rejectedMiss, Is.Null);
        }
    }

    [Test]
    public void LeafBlobEstimatesMatchPartitionPayloads()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PbtStoreCache.EstimateLeafBlobSize(PbtPartition.Account), Is.EqualTo(4 * 1024));
            Assert.That(PbtStoreCache.EstimateLeafBlobSize(PbtPartition.Code), Is.EqualTo(4 * 1024));
            Assert.That(PbtStoreCache.EstimateLeafBlobSize(PbtPartition.Storage), Is.EqualTo(40));
        }
    }

    [TestCase(PbtTrieLayout.FourLevelEveryLevel, 977)]
    [TestCase(PbtTrieLayout.FourLevelInterleaved, 657)]
    [TestCase(PbtTrieLayout.FourLevelBoundaryOnly, 529)]
    [TestCase(PbtTrieLayout.FiveLevelInterleaved, 1371)]
    [TestCase(PbtTrieLayout.SixLevelInterleaved, 2735)]
    [TestCase(PbtTrieLayout.SixLevelEvery3Depth, 2351)]
    [TestCase(PbtTrieLayout.EightLevelInterleaved, 11049)]
    [TestCase(PbtTrieLayout.EightLevelEvery4Depth, 8361)]
    public void TrieNodeEstimateIsDerivedFromLayout(PbtTrieLayout layout, int expected) =>
        Assert.That(PbtStoreCache.EstimateTrieNodeSize(layout), Is.EqualTo(expected));

    private static PbtConfig ConfigWithBudget(bool trieNode, PbtPartition partition, ulong budget)
    {
        PbtConfig config = EmptyConfig();
        if (trieNode)
        {
            if (partition == PbtPartition.Account) config.AccountTrieNodeCacheSizeBudget = budget;
            else if (partition == PbtPartition.Code) config.CodeTrieNodeCacheSizeBudget = budget;
            else config.StorageTrieNodeCacheSizeBudget = budget;
        }
        else
        {
            if (partition == PbtPartition.Account) config.AccountLeafBlobCacheSizeBudget = budget;
            else if (partition == PbtPartition.Code) config.CodeLeafBlobCacheSizeBudget = budget;
            else config.StorageLeafBlobCacheSizeBudget = budget;
        }
        return config;
    }

    private static PbtConfig EmptyConfig() => new()
    {
        AccountLeafBlobCacheSizeBudget = 0,
        CodeLeafBlobCacheSizeBudget = 0,
        StorageLeafBlobCacheSizeBudget = 0,
        AccountTrieNodeCacheSizeBudget = 0,
        CodeTrieNodeCacheSizeBudget = 0,
        StorageTrieNodeCacheSizeBudget = 0,
    };

    private static void Set(PbtStoreCache cache, bool trieNode, in Stem stem, in ValueHash256 hash, RefCountingMemory memory)
    {
        if (trieNode) cache.SetTrieNode(TrieNodeKey.For(24, stem), hash, memory);
        else cache.SetLeafBlob(stem, hash, memory);
    }

    private static RefCountingMemory? Get(PbtStoreCache cache, bool trieNode, in Stem stem, in ValueHash256 hash) =>
        trieNode ? cache.GetTrieNode(TrieNodeKey.For(24, stem), hash) : cache.GetLeafBlob(stem, hash);

    private static Stem StemFor(PbtPartition partition, byte marker)
    {
        byte[] bytes = new byte[Stem.Length];
        bytes[0] = partition switch
        {
            PbtPartition.Account => marker,
            PbtPartition.Code => 0x10,
            _ => 0x80,
        };
        bytes[1] = marker;
        return new Stem(bytes);
    }

    private static Stem StemInShard(PbtPartition partition, byte shardBits, ushort marker)
    {
        int rootDepth = PbtPartitions.RootDepth(partition);
        int variableBits = 8 - rootDepth;
        Stem stem = StemBelowRoot(partition, shardBits);
        byte[] bytes = stem.Bytes.ToArray();
        bytes[1] |= (byte)(marker & (0xFF >> rootDepth));
        bytes[2] = (byte)(marker >> variableBits);
        return new Stem(bytes);
    }

    private static Stem[] StemsInDistinctCacheSlots(bool trieNode, int count)
    {
        Stem?[] bySlot = new Stem?[16];
        int found = 0;
        for (int marker = 1; marker <= ushort.MaxValue && found < count; marker++)
        {
            Stem stem = StemInShard(PbtPartition.Account, 1, (ushort)marker);
            int slot = (trieNode ? TrieNodeKey.For(24, stem).GetHashCode() : stem.GetHashCode()) & 15;
            if (bySlot[slot] is not null) continue;
            bySlot[slot] = stem;
            found++;
        }

        if (found < count) throw new InvalidOperationException($"Could not find {count} distinct cache slots");

        Stem[] result = new Stem[count];
        int resultIndex = 0;
        for (int slot = 0; slot < bySlot.Length && resultIndex < count; slot++)
        {
            if (bySlot[slot] is Stem stem) result[resultIndex++] = stem;
        }
        return result;
    }

    /// <summary>A stem whose eight bits below its partition's fixed prefix are <paramref name="shardBits"/>.</summary>
    /// <remarks>
    /// 0 and 1 are the values that fit under every partition's prefix, so stems built from them differ
    /// nowhere else — including in the first byte, which the cache used to shard on.
    /// </remarks>
    private static Stem StemBelowRoot(PbtPartition partition, byte shardBits)
    {
        int rootDepth = PbtPartitions.RootDepth(partition);
        byte prefix = partition switch
        {
            PbtPartition.Account => 0x00,
            PbtPartition.Code => 0x10,
            _ => 0x80,
        };

        byte[] bytes = new byte[Stem.Length];
        bytes[0] = (byte)(prefix | (shardBits >> rootDepth));
        bytes[1] = (byte)(shardBits << (8 - rootDepth));
        return new Stem(bytes);
    }

    private static ValueHash256 Hash(byte marker)
    {
        byte[] bytes = new byte[32];
        bytes[0] = marker;
        return new ValueHash256(bytes);
    }

    private static PbtSnapshotMemoryLabel StoreCacheLabel(bool trieNode, PbtPartition partition) => new(
        partition.ToString().ToLowerInvariant(), trieNode ? "trie" : "leaf");

    private static void AssertMetrics(PbtSnapshotMemoryLabel label, long memory, long hits, long misses)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.PbtStoreCacheMemory[label], Is.EqualTo(memory), "memory");
            Assert.That(Metrics.PbtStoreCacheHits[label], Is.EqualTo(hits), "hits");
            Assert.That(Metrics.PbtStoreCacheMisses[label], Is.EqualTo(misses), "misses");
        }
    }

    private static RefCountingMemory Memory(params byte[] bytes) => RefCountingMemory.Wrapping(bytes);

    private static RefCountingMemory SizedMemory(int size, byte value)
    {
        byte[] bytes = new byte[size];
        Array.Fill(bytes, value);
        return RefCountingMemory.Wrapping(bytes);
    }
}
