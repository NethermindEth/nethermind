// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Reflection;
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

        Assert.That(store.Get(header.Hash!), Is.Null);
        Assert.That(store.Get(header2.Hash!), Is.Null);

        store.Insert(header);
        Assert.That(store.Get(header.Hash!)!.Hash, Is.EqualTo(header.Hash!));
        Assert.That(store.Get(header.Hash!, blockNumber: header.Number)!.Hash, Is.EqualTo(header.Hash!));
        Assert.That(store.Get(header2.Hash!), Is.Null);

        store.Insert(header2);
        Assert.That(store.Get(header.Hash!)!.Hash, Is.EqualTo(header.Hash!));
        Assert.That(store.Get(header2.Hash!, blockNumber: header2.Number)!.Hash, Is.EqualTo(header2.Hash!));
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
            Assert.That(result?.Hash, Is.EqualTo(persisted ? context.Header.Hash : null));
            Assert.That(result, Is.Not.SameAs(context.Header));
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
            int expectedNumberReads = i == 1 && shouldCache ? 0 : 1;
            context.AssertReads(() => Assert.That(
                context.Store.Get(context.Header.Hash!, shouldCache)?.Hash, Is.EqualTo(context.Header.Hash)),
                numberReads: expectedNumberReads, headerReads: i == 0 ? 1 : 0);
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

    [Test]
    public void Encoded_header_reads_reuse_bytes_without_sharing_mutable_headers([Values] bool firstBloom, [Values] bool secondBloom)
    {
        TestMemDb db = new();
        CountingHeaderDecoder decoder = new();
        HeaderStore store = new(db, new MemDb(), decoder);
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).WithExtraData([1, 2, 3]).TestObject;
        store.Insert(header);
        Hash256 lookup = new(header.Hash!.ValueHash256);

        BlockHeader first = ReadHeader(store, lookup, header.Number, firstBloom)!;
        long reads = db.ReadsCount;
        first.Number = 999;
        first.ExtraData[0] = 99;
        first.Hash!.Bytes[0] ^= 1;
        lookup.Bytes[0] ^= 1;
        BlockHeader second = ReadHeader(store, header.Hash!, header.Number, secondBloom)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second, Is.EqualTo(header).UsingBlockHeaderComparer());
            Assert.That(db.ReadsCount, Is.EqualTo(reads));
            Assert.That(decoder.Decodes, Is.EqualTo(2), "encoded hits must still decode and validate");
            Assert.That(decoder.LastBehaviors, Is.EqualTo(RlpBehaviors.AllowExtraBytes | RlpBehaviors.MemoizeHeaderHash));
        }
    }

    [Test]
    public void Encoded_header_key_preserves_number_hints_and_legacy_fallback([Values] bool legacy, [Values] bool bloom)
    {
        TestMemDb db = new();
        HeaderStore store = new(db, new MemDb());
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        if (legacy) db.Set(header.Hash!, new HeaderDecoder().Encode(header).Bytes);
        else store.Insert(header);
        Assert.That(ReadHeader(store, header.Hash!, header.Number, bloom)?.Hash, Is.EqualTo(header.Hash));

        Assert.That(ReadHeader(store, header.Hash!, 999, bloom)?.Hash, Is.EqualTo(legacy ? header.Hash : null));
        store.InsertBlockNumber(header.Hash!, 999);
        Assert.That(store.Get(header.Hash!)?.Hash, Is.EqualTo(legacy ? header.Hash : null));
        store.InsertBlockNumber(header.Hash!, header.Number);
        Assert.That(store.Get(header.Hash!)?.Hash, Is.EqualTo(header.Hash));
    }

    [Test]
    public void Encoded_headers_do_not_cache_missing_or_malformed_rows([Values] bool bloom)
    {
        TestMemDb db = new();
        HeaderStore store = new(db, new MemDb());
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        Assert.That(ReadHeader(store, header.Hash!, header.Number, bloom), Is.Null);
        db.Set(header.Number, header.Hash!, new byte[] { 0xf8, 0xff });
        Assert.Throws(bloom ? typeof(IndexOutOfRangeException) : typeof(RlpException), () => ReadHeader(store, header.Hash!, header.Number, bloom));
        db.Set(header.Number, header.Hash!, new HeaderDecoder().Encode(header).Bytes);

        Assert.That(ReadHeader(store, header.Hash!, header.Number, bloom)?.Hash, Is.EqualTo(header.Hash));
    }

    [Test]
    public void Encoded_header_hits_preserve_key_value_mismatch_and_extra_bytes([Values] bool bloom)
    {
        TestMemDb db = new();
        HeaderStore store = new(db, new MemDb());
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        BlockHeader different = Build.A.BlockHeader.WithNumber(100).WithExtraData([9]).TestObject;
        byte[] encoded = new HeaderDecoder().Encode(different).Bytes;
        byte[] withExtra = new byte[encoded.Length + 1];
        encoded.CopyTo(withExtra, 0);
        withExtra[^1] = 0xff;
        db.Set(header.Number, header.Hash!, withExtra);

        for (int i = 0; i < 2; i++)
        {
            Assert.That(ReadHeader(store, header.Hash!, header.Number, bloom)?.Hash, Is.EqualTo(different.Hash));
        }
        Assert.That(db.ReadsCount, Is.EqualTo(1));
    }

    private static readonly string[] EncodedHeaderMutations = ["Insert", "BulkInsert", "Delete", "ClearCache", "Cache"];

    [Test]
    public void Encoded_headers_are_invalidated_by_mutation([ValueSource(nameof(EncodedHeaderMutations))] string mutation)
    {
        TestMemDb db = new();
        HeaderStore store = new(db, new MemDb());
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        store.Insert(header);
        Assert.That(store.Get(header.Hash!, blockNumber: header.Number), Is.Not.Null);
        BlockHeader replacement = header.Clone();
        replacement.GasUsed = 7;

        MutateHeader(store, db, header, replacement, mutation);

        BlockHeader? result = store.Get(header.Hash!, blockNumber: header.Number);
        Assert.That(result?.GasUsed, Is.EqualTo(mutation is "Delete" or "ClearCache" ? (ulong?)null : 7UL));
    }

    [Test]
    public void Encoded_headers_do_not_refill_after_a_decoder_mutates_the_store(
        [ValueSource(nameof(EncodedHeaderMutations))] string mutation)
    {
        TestMemDb db = new();
        CountingHeaderDecoder decoder = new();
        HeaderStore store = new(db, new MemDb(), decoder);
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        store.Insert(header);
        BlockHeader replacement = header.Clone();
        replacement.GasUsed = 7;
        decoder.AfterDecode = () =>
        {
            decoder.AfterDecode = null;
            MutateHeader(store, db, header, replacement, mutation);
        };

        Assert.That(store.Get(header.Hash!, blockNumber: header.Number)?.GasUsed, Is.EqualTo(header.GasUsed));

        BlockHeader? result = store.Get(header.Hash!, blockNumber: header.Number);
        Assert.That(result?.GasUsed, Is.EqualTo(mutation is "Delete" or "ClearCache" ? (ulong?)null : 7UL));
    }

    [Test]
    public void Encoded_headers_do_not_refill_after_a_bloom_callback_deletes_the_header([Values] bool warm)
    {
        TestMemDb db = new();
        HeaderStore store = new(db, new MemDb());
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        store.Insert(header);
        if (warm) Assert.That(store.Get(header.Hash!, blockNumber: header.Number), Is.Not.Null);

        BlockHeader? result = store.GetIfBloomMatches(header.Hash!, header.Number, _ =>
        {
            store.Delete(header.Hash!);
            return true;
        }, out bool found);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(result?.Hash, Is.EqualTo(header.Hash));
            Assert.That(store.Get(header.Hash!, blockNumber: header.Number), Is.Null);
        }
    }

    [Test]
    public void Encoded_header_reads_release_borrowed_memory_and_keep_owned_bytes(
        [Values] bool native, [Values] bool bloom, [Values] bool decoderThrows)
    {
        BorrowedHeaderDb db = native ? new NativeBorrowedHeaderDb() : new BorrowedHeaderDb();
        CountingHeaderDecoder decoder = new();
        HeaderStore store = new(db, new MemDb(), decoder);
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        store.Insert(header);
        if (decoderThrows)
        {
            decoder.AfterDecode = () => throw new InvalidDataException("Decoder failed.");
            Assert.Throws<InvalidDataException>(() => ReadHeader(store, header.Hash!, header.Number, bloom));
            Assert.That(db.Releases, Is.EqualTo(1));
            decoder.AfterDecode = null;
        }

        Assert.That(ReadHeader(store, header.Hash!, header.Number, bloom)?.Hash, Is.EqualTo(header.Hash));
        Assert.That(ReadHeader(store, header.Hash!, header.Number, bloom)?.Hash, Is.EqualTo(header.Hash));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.Releases, Is.EqualTo(decoderThrows ? 2 : 1));
            Assert.That(db.ReadsCount, Is.EqualTo(decoderThrows ? 2 : 1));
        }
    }

    [Test]
    public void Header_writes_hold_the_encoded_cache_lock_through_batch_commit([Values] bool bulk)
    {
        TestMemDb db = new();
        HeaderStore store = new(db, new MemDb());
        EncodedRlpCache<(ValueHash256 Hash, ulong? Number)> cache =
            (EncodedRlpCache<(ValueHash256 Hash, ulong? Number)>)typeof(HeaderStore)
                .GetField("_encodedRlp", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        int writes = 0;
        db.WriteFunc = (_, _) =>
        {
            Assert.That(cache.SyncRoot.IsHeldByCurrentThread, Is.True);
            writes++;
            return true;
        };

        if (bulk) store.BulkInsert([header]);
        else store.Insert(header);

        Assert.That(writes, Is.EqualTo(1));
    }

    [Test]
    public void Encoded_header_reads_release_empty_or_malformed_borrowed_memory(
        [Values] bool native, [Values] bool bloom, [Values] bool empty)
    {
        BorrowedHeaderDb db = native ? new NativeBorrowedHeaderDb() : new BorrowedHeaderDb();
        HeaderStore store = new(db, new MemDb());
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        db.ReadFunc = _ => empty ? [] : [0xf8, 0xff];

        if (empty) Assert.That(ReadHeader(store, header.Hash!, header.Number, bloom), Is.Null);
        else Assert.Throws(bloom ? typeof(IndexOutOfRangeException) : typeof(RlpException), () => ReadHeader(store, header.Hash!, header.Number, bloom));

        Assert.That(db.Releases, Is.EqualTo(empty ? 2 : 1));
    }

    [Test]
    public void Empty_prefixed_headers_still_fall_back_to_legacy_rows([Values] bool bloom)
    {
        TestMemDb db = new();
        HeaderStore store = new(db, new MemDb());
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        db.Set(header.Number, header.Hash!, Array.Empty<byte>());
        db.Set(header.Hash!, new HeaderDecoder().Encode(header).Bytes);

        Assert.That(ReadHeader(store, header.Hash!, header.Number, bloom)?.Hash, Is.EqualTo(header.Hash));
        Assert.That(ReadHeader(store, header.Hash!, header.Number, bloom)?.Hash, Is.EqualTo(header.Hash));
        Assert.That(db.ReadsCount, Is.EqualTo(3));
    }

    private static BlockHeader? ReadHeader(HeaderStore store, Hash256 hash, ulong number, bool bloom) =>
        bloom ? store.GetIfBloomMatches(hash, number, static _ => true, out _) : store.Get(hash, blockNumber: number);

    private static void MutateHeader(HeaderStore store, TestMemDb db, BlockHeader header, BlockHeader replacement, string mutation)
    {
        switch (mutation)
        {
            case "Insert": store.Insert(replacement); break;
            case "BulkInsert": store.BulkInsert([replacement]); break;
            case "Delete": store.Delete(header.Hash!); break;
            case "ClearCache":
                db.Clear();
                ((IClearableCache)store).ClearCache();
                break;
            case "Cache": store.Cache(replacement); break;
        }
    }

    private class BorrowedHeaderDb : TestMemDb, IReadOnlyKeyValueStore
    {
        private byte[]? _borrowed;
        public int Releases { get; private set; }

        public Span<byte> Borrow(scoped ReadOnlySpan<byte> key, ReadFlags flags)
        {
            byte[]? bytes = Get(key, flags);
            _borrowed = bytes is null ? null : (byte[])bytes.Clone();
            return _borrowed;
        }

        public void Release()
        {
            Releases++;
            _borrowed!.AsSpan().Fill(0);
            _borrowed = null;
        }

        Span<byte> IReadOnlyKeyValueStore.GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags) => Borrow(key, flags);
        void IReadOnlyKeyValueStore.DangerousReleaseMemory(in ReadOnlySpan<byte> span) => Release();
    }

    private sealed class NativeBorrowedHeaderDb : BorrowedHeaderDb, IReadOnlyNativeKeyValueStore
    {
        public ReadOnlySpan<byte> GetNativeSlice(scoped ReadOnlySpan<byte> key, out nint handle, ReadFlags flags)
        {
            handle = 0;
            return Borrow(key, flags);
        }
        public void DangerousReleaseHandle(nint handle) => Release();
    }

    private sealed class CountingHeaderDecoder : HeaderDecoder
    {
        public int Decodes { get; private set; }
        public RlpBehaviors LastBehaviors { get; private set; }
        public Action? AfterDecode { get; set; }

        protected override BlockHeader? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            Decodes++;
            LastBehaviors = rlpBehaviors;
            BlockHeader? header = base.DecodeInternal(ref decoderContext, rlpBehaviors);
            AfterDecode?.Invoke();
            return header;
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
        Assert.That(partial[0].Hash, Is.EqualTo(chain[5].Hash!));
        Assert.That(partial[^1].Hash, Is.EqualTo(chain[7].Hash!));

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

        Assert.That(store.PrefetchByNumberRange(100, 101), Is.Empty);
        Assert.That(store.Get(header.Hash!)!.Hash, Is.EqualTo(header.Hash!));
    }

}
