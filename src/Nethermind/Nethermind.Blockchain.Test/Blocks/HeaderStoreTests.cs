// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
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
        HeaderStore store = new(new MemDb(), new MemDb());

        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        store.Cache(header);
        Assert.That(store.Get(header.Hash!)!.Hash, Is.EqualTo(header.Hash!));
    }

    [Test]
    public void TestCanDeleteHeader()
    {
        HeaderStore store = new(new MemDb(), new MemDb());
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        store.Insert(header);
        store.Delete(header.Hash!);

        Assert.That(store.Get(header.Hash!), Is.Null);
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
    public void TestClearCache_removes_cached_headers()
    {
        HeaderStore store = new(new MemDb(), new MemDb());

        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;

        // Cache the header (not inserted to DB)
        store.Cache(header);
        Assert.That(store.Get(header.Hash!)!.Hash, Is.EqualTo(header.Hash!));

        // Clear the cache - header should no longer be retrievable
        (store as IClearableCache)?.ClearCache();
        Assert.That(store.Get(header.Hash!), Is.Null);
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

}
