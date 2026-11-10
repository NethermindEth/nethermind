// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Blocks;

[Parallelizable(ParallelScope.All)]
public class HeaderStoreTests
{
    [Test]
    public void TestCanStoreAndGetHeader()
    {
        HeaderStore store = new(new MemDb(), new MemDb());

        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        BlockHeader header2 = Build.A.BlockHeader.WithNumber(102).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(header.Hash!), Is.Null);
            Assert.That(store.Get(header2.Hash!), Is.Null);
        }

        store.Insert(header);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(header.Hash!)!.Hash, Is.EqualTo(header.Hash!));
            Assert.That(store.Get(header.Hash!, blockNumber: header.Number)!.Hash, Is.EqualTo(header.Hash!));
            Assert.That(store.Get(header2.Hash!), Is.Null);
        }

        store.Insert(header2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(header.Hash!)!.Hash, Is.EqualTo(header.Hash!));
            Assert.That(store.Get(header2.Hash!, blockNumber: header2.Number)!.Hash, Is.EqualTo(header2.Hash!));
        }
    }

    [Test]
    public void TestCanReadHeaderStoredWithHash()
    {
        IDb headerDb = new MemDb();
        HeaderStore store = new(headerDb, new MemDb());

        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        headerDb.Set(header.Hash!, new HeaderDecoder().Encode(header).Bytes);

        Assert.That(store.Get(header.Hash!)!.Hash, Is.EqualTo(header.Hash!));
    }

    [Test]
    public void TestCanReadCacheHeader()
    {
        using HeaderStoreFixture context = new(cached: true);
        Assert.That(context.Store.Get(context.Header.Hash!), Is.SameAs(context.Header));
        context.AssertReads(() => Assert.That(context.Store.GetBlockNumber(context.Header.Hash!), Is.EqualTo(100)), numberReads: 1);
    }

    [Test]
    public void TestCanDeleteHeader([Values] bool cacheBeforeDelete)
    {
        using HeaderStoreFixture context = new(persisted: true, cached: cacheBeforeDelete);
        context.Store.Delete(context.Header.Hash!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Store.Get(context.Header.Hash!), Is.Null);
            Assert.That(context.Store.GetBlockNumber(context.Header.Hash!), Is.Null);
        }
    }

    [Test]
    public void TestCanDeleteHeaderStoredWithHash()
    {
        IDb headerDb = new MemDb();
        HeaderStore store = new(headerDb, new MemDb());

        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        headerDb.Set(header.Hash!, new HeaderDecoder().Encode(header).Bytes);

        store.Delete(header.Hash!);
        Assert.That(store.Get(header.Hash!)!, Is.Null);
    }

    [Test]
    public void TestClearCache_removes_cached_headers([Values] bool persisted)
    {
        using HeaderStoreFixture context = new(persisted: persisted, cached: true);
        Assert.That(context.Store.Get(context.Header.Hash!), Is.SameAs(context.Header));
        ((IClearableCache)context.Store).ClearCache();

        context.AssertReads(() =>
        {
            BlockHeader? result = context.Store.Get(context.Header.Hash!);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result?.Hash, Is.EqualTo(persisted ? context.Header.Hash : null));
                Assert.That(result, Is.Not.SameAs(context.Header));
            }
        }, numberReads: 1, headerReads: 1);
    }

    [Test]
    public void Get_on_cache_hit_reads_no_database(
        [Values] bool persisted, [Values] bool shouldCache, [Values(null, 100UL, 999UL)] ulong? blockNumber)
    {
        using HeaderStoreFixture context = new(persisted: persisted, cached: true);
        context.AssertReads(() => Assert.That(
            context.Store.Get(context.Header.Hash!, shouldCache, blockNumber), Is.SameAs(context.Header)));
    }

    [Test]
    public void Get_on_cache_miss_reads_databases_and_fills_cache_only_when_asked(
        [Values] bool legacyKey, [Values] bool shouldCache)
    {
        using HeaderStoreFixture context = new(persisted: !legacyKey);
        if (legacyKey) context.HeaderDb.Set(context.Header.Hash!, new HeaderDecoder().Encode(context.Header).Bytes);

        for (int i = 0; i < 2; i++)
        {
            int expectedReads = i == 1 && shouldCache ? 0 : 1;
            context.AssertReads(() => Assert.That(
                context.Store.Get(context.Header.Hash!, shouldCache)?.Hash, Is.EqualTo(context.Header.Hash)),
                numberReads: expectedReads, headerReads: expectedReads);
        }
    }

    [Test]
    public void Malformed_block_number_entry([Values] bool cached)
    {
        using HeaderStoreFixture context = new(persisted: true, cached: cached);
        context.BlockNumberDb.Set(context.Header.Hash!, new byte[7]);

        using (Assert.EnterMultipleScope())
        {
            if (cached) Assert.That(context.Store.Get(context.Header.Hash!), Is.SameAs(context.Header));
            else Assert.Throws<InvalidDataException>(() => context.Store.Get(context.Header.Hash!));
            Assert.Throws<InvalidDataException>(() => context.Store.GetBlockNumber(context.Header.Hash!));
        }
    }

    [Test]
    public void GetBlockNumber_prefers_the_persisted_mapping()
    {
        using HeaderStoreFixture context = new(persisted: true, cached: true);
        context.Store.InsertBlockNumber(context.Header.Hash!, 200);

        Assert.That(context.Store.GetBlockNumber(context.Header.Hash!), Is.EqualTo(200));
        context.Header.Number = 300;
        Assert.That(context.Store.GetBlockNumber(context.Header.Hash!), Is.EqualTo(200));
    }

    [Test]
    public void GetIfBloomMatches_decodes_only_a_header_whose_bloom_matches(
        [Values] bool cached, [Values] bool legacyKey, [Values] bool matches)
    {
        CountingHeaderDecoder decoder = new();
        MemDb headerDb = new();
        HeaderStore store = new(headerDb, new MemDb(), decoder);
        Bloom bloom = new();
        bloom.Set(TestItem.AddressA.Bytes);
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).WithBloom(bloom).TestObject;
        if (legacyKey) headerDb.Set(header.Hash!, decoder.Encode(header).Bytes);
        else store.Insert(header);
        if (cached) store.Cache(header);
        Address address = matches ? TestItem.AddressA : TestItem.AddressB;

        BlockHeader? result = store.GetIfBloomMatches(header.Hash!, header.Number, b => new BloomStructRef(b).Matches(address.Bytes), out bool found);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(result?.Hash, Is.EqualTo(matches ? header.Hash : null));
            Assert.That(result?.Bloom, Is.EqualTo(matches ? bloom : null));
            Assert.That(decoder.Decodes, Is.EqualTo(matches && !cached ? 1 : 0));
        }
    }

    [Test]
    public void GetIfBloomMatches_reports_a_missing_header()
    {
        HeaderStore store = new(new MemDb(), new MemDb());
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        bool tested = false;

        BlockHeader? result = store.GetIfBloomMatches(header.Hash!, header.Number, _ => tested = true, out bool found);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(found, Is.False);
            Assert.That(tested, Is.False);
        }
    }

    [Test]
    public void GetIfBloomMatches_passes_a_zero_length_bloom_as_empty()
    {
        IReleaseSpec spec = new OverridableReleaseSpec(Bogota.Instance) { IsEip7668Enabled = true };
        HeaderStore store = new(new MemDb(), new MemDb(), new HeaderDecoder(new TestSingleReleaseSpecProvider(spec)));
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).WithBloom(Bloom.ZeroLength).TestObject;
        store.Insert(header);

        BlockHeader? result = store.GetIfBloomMatches(header.Hash!, header.Number, static b => b.IsEmpty, out bool found);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(result?.Bloom, Is.SameAs(Bloom.ZeroLength));
        }
    }

    private sealed class CountingHeaderDecoder : HeaderDecoder
    {
        public int Decodes { get; private set; }

        protected override BlockHeader? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            Decodes++;
            return base.DecodeInternal(ref decoderContext, rlpBehaviors);
        }
    }

    private sealed class HeaderStoreFixture : IDisposable
    {
        public MemDb HeaderDb { get; } = new();
        public MemDb BlockNumberDb { get; } = new();
        public BlockHeader Header { get; } = Build.A.BlockHeader.WithNumber(100).TestObject;
        public HeaderStore Store { get; }

        public HeaderStoreFixture(bool persisted = false, bool cached = false)
        {
            Store = new(HeaderDb, BlockNumberDb);
            if (persisted) Store.Insert(Header);
            if (cached) Store.Cache(Header);
        }

        public void AssertReads(Action action, long numberReads = 0, long headerReads = 0)
        {
            long numbersBefore = BlockNumberDb.ReadsCount;
            long headersBefore = HeaderDb.ReadsCount;
            using (Assert.EnterMultipleScope())
            {
                action();
                Assert.That(BlockNumberDb.ReadsCount - numbersBefore, Is.EqualTo(numberReads), "block number reads");
                Assert.That(HeaderDb.ReadsCount - headersBefore, Is.EqualTo(headerReads), "header reads");
            }
        }

        public void Dispose()
        {
            HeaderDb.Dispose();
            BlockNumberDb.Dispose();
        }
    }

    // Parameterized: true = iterator-capable backend (TestMemDb), false = plain MemDb fallback
    [TestCase(true, Description = "Iterator-capable backend")]
    [TestCase(false, Description = "Plain MemDb fallback")]
    public void FindReversedHeaders_returns_chain_oldest_first(bool useIteratorBackend)
    {
        IDb headerDb = useIteratorBackend ? new TestMemDb() : new MemDb();
        HeaderStore store = new(headerDb, new MemDb());

        // Build chain: genesis ← h1 ← h2 ← ... ← h7 (8 headers total, numbers 0..7)
        BlockHeader[] chain = new BlockHeader[8];
        chain[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        for (int i = 1; i < chain.Length; i++)
            chain[i] = Build.A.BlockHeader.WithParent(chain[i - 1]).TestObject;
        foreach (BlockHeader h in chain) store.Insert(h);

        BlockHeader last = chain[^1];
        using IOwnedReadOnlyList<BlockHeader> result = store.FindReversedHeaders(last.Number, last.Hash!, chain.Length);

        Assert.That(result.Count, Is.EqualTo(chain.Length));
        for (int i = 0; i < chain.Length; i++)
            Assert.That(result[i].Hash, Is.EqualTo(chain[i].Hash!));

        // Unknown hash → empty
        using IOwnedReadOnlyList<BlockHeader> empty = store.FindReversedHeaders(last.Number, Keccak.Zero, chain.Length);
        Assert.That(empty.Count, Is.EqualTo(0));

        // Gap: remove header at chain[4], walk from chain[7] should stop at chain[5]
        store.Delete(chain[4].Hash!);
        using IOwnedReadOnlyList<BlockHeader> partial = store.FindReversedHeaders(last.Number, last.Hash!, chain.Length);
        Assert.That(partial.Count, Is.EqualTo(3)); // chain[5], chain[6], chain[7]
        using (Assert.EnterMultipleScope())
        {
            Assert.That(partial[0].Hash, Is.EqualTo(chain[5].Hash!));
            Assert.That(partial[^1].Hash, Is.EqualTo(chain[7].Hash!));
        }

        // Fork: insert an extra header at the same number as chain[3] that is NOT in the main chain
        BlockHeader fork = Build.A.BlockHeader.WithParent(chain[2]).TestObject;
        store.Insert(fork);
        // Re-insert chain[4] to restore the gap
        store.Insert(chain[4]);
        // Walk should still follow the main chain (chain[6].ParentHash = chain[5].Hash, etc.)
        using IOwnedReadOnlyList<BlockHeader> withFork = store.FindReversedHeaders(last.Number, last.Hash!, chain.Length);
        Assert.That(withFork.Count, Is.EqualTo(chain.Length));
        Assert.That(withFork, Does.Not.Contain(fork));
    }

    [Test]
    public void TestPrefetchByNumberRangeReadsOnlyTheRequestedRange()
    {
        HeaderStore store = new(new TestMemDb(), new MemDb());

        BlockHeader below = Build.A.BlockHeader.WithNumber(99).TestObject;
        BlockHeader first = Build.A.BlockHeader.WithNumber(100).TestObject;
        BlockHeader last = Build.A.BlockHeader.WithNumber(101).TestObject;
        BlockHeader above = Build.A.BlockHeader.WithNumber(102).TestObject;

        store.Insert(below);
        store.Insert(first);
        store.Insert(last);
        store.Insert(above);

        Assert.That(
            store.PrefetchByNumberRange(100, 102).Keys,
            Is.EquivalentTo(new[] { first.Hash!.ValueHash256, last.Hash!.ValueHash256 }));
    }

    [Test]
    public void TestPrefetchByNumberRangeReturnsNothingWhenTheStoreCannotScan()
    {
        HeaderStore store = new(new MemDb(), new MemDb());
        store.Insert(Build.A.BlockHeader.WithNumber(100).TestObject);

        Assert.That(store.PrefetchByNumberRange(100, 101), Is.Empty);
    }

    [Test]
    public void TestPrefetchByNumberRangeSkipsAHeaderHeldUnderALegacyHashOnlyKey()
    {
        IDb headerDb = new TestMemDb();
        HeaderStore store = new(headerDb, new MemDb());

        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        headerDb.Set(header.Hash!, new HeaderDecoder().Encode(header).Bytes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.PrefetchByNumberRange(100, 101), Is.Empty);
            Assert.That(store.Get(header.Hash!)!.Hash, Is.EqualTo(header.Hash!));
        }
    }

}
