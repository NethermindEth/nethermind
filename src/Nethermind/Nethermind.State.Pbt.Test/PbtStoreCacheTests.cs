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
        using PbtStoreCache cache = new(new PbtConfig());
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

    [Test]
    public void TrieNodeMetricsTrackExactDeltasAndExcludeLeafBlobs()
    {
        long memoryBaseline = Metrics.PbtTrieNodeCacheMemory;
        long hitBaseline = Metrics.PbtTrieNodeCacheHits;
        long missBaseline = Metrics.PbtTrieNodeCacheMisses;
        using PbtStoreCache cache = new(new PbtConfig());
        Stem stem = StemFor(PbtPartition.Account, 1);
        TrieNodeKey key = TrieNodeKey.For(16, stem);
        ValueHash256 hash = Hash(1);
        ValueHash256 otherHash = Hash(2);
        using RefCountingMemory leaf = Memory(2, 3);
        using RefCountingMemory first = Memory(4, 5, 6);
        using RefCountingMemory replacement = Memory(7, 8, 9, 10, 11);

        cache.SetLeafBlob(stem, hash, leaf);
        using RefCountingMemory? leafHit = cache.GetLeafBlob(stem, hash);
        AssertMetrics(memoryBaseline, hitBaseline, missBaseline);

        using RefCountingMemory? miss = cache.GetTrieNode(key, hash);
        AssertMetrics(memoryBaseline, hitBaseline, missBaseline + 1);

        cache.SetTrieNode(key, hash, first);
        AssertMetrics(memoryBaseline + 3, hitBaseline, missBaseline + 1);

        using RefCountingMemory? hit = cache.GetTrieNode(key, hash);
        AssertMetrics(memoryBaseline + 3, hitBaseline + 1, missBaseline + 1);

        using RefCountingMemory? wrongHash = cache.GetTrieNode(key, otherHash);
        Assert.That(wrongHash, Is.Null);
        AssertMetrics(memoryBaseline + 3, hitBaseline + 1, missBaseline + 2);

        cache.SetTrieNode(key, hash, replacement);
        AssertMetrics(memoryBaseline + 5, hitBaseline + 1, missBaseline + 2);

        cache.Dispose();
        AssertMetrics(memoryBaseline, hitBaseline + 1, missBaseline + 2);
    }

    [Test]
    public void TrieNodeMemoryMetricTracksPruning()
    {
        long memoryBaseline = Metrics.PbtTrieNodeCacheMemory;
        using PbtStoreCache cache = new(ConfigWithBudget(true, PbtPartition.Account, 1));
        Stem firstStem = StemFor(PbtPartition.Account, 1);
        Stem secondStem = StemFor(PbtPartition.Account, 2);
        using RefCountingMemory first = Memory(1);
        using RefCountingMemory second = Memory(2);

        cache.SetTrieNode(TrieNodeKey.For(16, firstStem), Hash(1), first);
        Assert.That(Metrics.PbtTrieNodeCacheMemory, Is.EqualTo(memoryBaseline + 1));

        cache.SetTrieNode(TrieNodeKey.For(16, secondStem), Hash(2), second);
        Assert.That(Metrics.PbtTrieNodeCacheMemory, Is.EqualTo(memoryBaseline + 1));

        cache.Dispose();
        Assert.That(Metrics.PbtTrieNodeCacheMemory, Is.EqualTo(memoryBaseline));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ZeroBudgetDisablesOnlyItsBucket(bool trieNode)
    {
        PbtConfig config = new()
        {
            AccountLeafBlobCacheSizeBudget = trieNode ? 1024UL : 0,
            AccountTrieNodeCacheSizeBudget = trieNode ? 0 : 1024UL,
        };
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
        PbtConfig config = ConfigWithBudget(trieNode, partition, 1);
        PbtStoreCache cache = new(config);
        Stem firstKey = StemFor(partition, 1);
        Stem secondKey = StemFor(partition, 2);
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
    public void ConcurrentAccessBalancesEveryLease(bool trieNode, PbtPartition partition)
    {
        const int operationCount = 128;
        long memoryBaseline = Metrics.PbtTrieNodeCacheMemory;
        long hitBaseline = Metrics.PbtTrieNodeCacheHits;
        long missBaseline = Metrics.PbtTrieNodeCacheMisses;
        using PbtStoreCache cache = new(ConfigWithBudget(trieNode, partition, 16));
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
        long recordedLookups = Metrics.PbtTrieNodeCacheHits - hitBaseline + Metrics.PbtTrieNodeCacheMisses - missBaseline;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(TrackingMemoryProvider.CountUnreleased(values), Is.Zero);
            Assert.That(Metrics.PbtTrieNodeCacheMemory, Is.EqualTo(memoryBaseline));
            Assert.That(recordedLookups, Is.EqualTo(trieNode ? operationCount : 0));
        }
    }

    [Test]
    public void ExhaustingOneBucketDoesNotEvictAnother()
    {
        PbtConfig config = new()
        {
            AccountLeafBlobCacheSizeBudget = 1,
            CodeLeafBlobCacheSizeBudget = 1,
        };
        using PbtStoreCache cache = new(config);
        Stem accountA = StemFor(PbtPartition.Account, 1);
        Stem accountB = StemFor(PbtPartition.Account, 2);
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

    private static PbtConfig ConfigWithBudget(bool trieNode, PbtPartition partition, ulong budget)
    {
        PbtConfig config = new()
        {
            AccountLeafBlobCacheSizeBudget = 0,
            CodeLeafBlobCacheSizeBudget = 0,
            StorageLeafBlobCacheSizeBudget = 0,
            AccountTrieNodeCacheSizeBudget = 0,
            CodeTrieNodeCacheSizeBudget = 0,
            StorageTrieNodeCacheSizeBudget = 0,
        };
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

    private static void Set(PbtStoreCache cache, bool trieNode, in Stem stem, in ValueHash256 hash, RefCountingMemory memory)
    {
        if (trieNode) cache.SetTrieNode(TrieNodeKey.For(16, stem), hash, memory);
        else cache.SetLeafBlob(stem, hash, memory);
    }

    private static RefCountingMemory? Get(PbtStoreCache cache, bool trieNode, in Stem stem, in ValueHash256 hash) =>
        trieNode ? cache.GetTrieNode(TrieNodeKey.For(16, stem), hash) : cache.GetLeafBlob(stem, hash);

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

    private static ValueHash256 Hash(byte marker)
    {
        byte[] bytes = new byte[32];
        bytes[0] = marker;
        return new ValueHash256(bytes);
    }

    private static void AssertMetrics(long memory, long hits, long misses)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.PbtTrieNodeCacheMemory, Is.EqualTo(memory), "memory");
            Assert.That(Metrics.PbtTrieNodeCacheHits, Is.EqualTo(hits), "hits");
            Assert.That(Metrics.PbtTrieNodeCacheMisses, Is.EqualTo(misses), "misses");
        }
    }

    private static RefCountingMemory Memory(params byte[] bytes) => RefCountingMemory.Wrapping(bytes);
}
