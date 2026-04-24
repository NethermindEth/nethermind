// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Blockchain.SkipIndexedBlockInfo;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class SkipIndexedBlockInfoStoreTests
{
    private const int ChainLength = 32;

    [TestCase(0L, 0L)]
    [TestCase(1L, 1L)]
    [TestCase(2L, 2L)]
    [TestCase(3L, 1L)]
    [TestCase(4L, 4L)]
    [TestCase(7L, 1L)]
    [TestCase(8L, 8L)]
    [TestCase(12L, 4L)]
    [TestCase(16L, 16L)]
    public void SkipDistance_returns_highest_power_of_two_divisor(long blockNumber, long expectedDistance) =>
        SkipIndexedBlockInfoStore.SkipDistance(blockNumber).Should().Be(expectedDistance);

    [Test]
    public void GetTotalDifficulty_genesis_returns_genesis_difficulty()
    {
        (SkipIndexedBlockInfoStore store, BlockHeader[] headers) = BuildChain(1);
        ValueHash256 genesisHash = headers[0].Hash!.ValueHash256;

        UInt256? td = store.GetTotalDifficulty(0, in genesisHash);

        td.Should().Be(headers[0].Difficulty);
    }

    [Test]
    public void GetTotalDifficulty_returns_cumulative_sum()
    {
        (SkipIndexedBlockInfoStore store, BlockHeader[] headers) = BuildChain(ChainLength);

        UInt256 expectedTd = UInt256.Zero;
        for (int i = 0; i < ChainLength; i++)
        {
            expectedTd += headers[i].Difficulty;
            ValueHash256 hash = headers[i].Hash!.ValueHash256;
            UInt256? td = store.GetTotalDifficulty(i, in hash);
            td.Should().Be(expectedTd, $"TD mismatch at block {i}");
        }
    }

    [Test]
    public void GetTotalDifficulty_returns_null_for_missing_header()
    {
        MemDb headersDb = new();
        MemDb blockNumbersDb = new();
        MemDb cumulativeDb = new();
        HeaderStore headerStore = new(headersDb, blockNumbersDb);
        SkipIndexedBlockInfoStore store = new(cumulativeDb, headerStore, new CumulativeTotalDifficultyStrategy(), NullTotalDifficultyAnchor.Instance, LimboLogs.Instance);

        // Query a hash that doesn't exist
        ValueHash256 fakeHash = new(Keccak.Compute("nonexistent").Bytes);
        UInt256? td = store.GetTotalDifficulty(5, in fakeHash);
        td.Should().BeNull();
    }

    [Test]
    public void GetAncestorAt_self_returns_own_hash()
    {
        (SkipIndexedBlockInfoStore store, BlockHeader[] headers) = BuildChain(ChainLength);

        for (int i = 0; i < ChainLength; i++)
        {
            ValueHash256 hash = headers[i].Hash!.ValueHash256;
            ValueHash256? result = store.GetAncestorAt(i, in hash, i);
            result.Should().Be(hash, $"Self-lookup failed at block {i}");
        }
    }

    [TestCase(10, 0)]
    [TestCase(10, 5)]
    [TestCase(10, 9)]
    [TestCase(31, 0)]
    [TestCase(31, 16)]
    [TestCase(16, 8)]
    [TestCase(8, 0)]
    [TestCase(1, 0)]
    public void GetAncestorAt_returns_correct_ancestor(int blockNumber, int ancestorNumber)
    {
        (SkipIndexedBlockInfoStore store, BlockHeader[] headers) = BuildChain(ChainLength);

        ValueHash256 hash = headers[blockNumber].Hash!.ValueHash256;
        ValueHash256 expected = headers[ancestorNumber].Hash!.ValueHash256;

        ValueHash256? result = store.GetAncestorAt(blockNumber, in hash, ancestorNumber);

        result.Should().Be(expected, $"GetAncestorAt({blockNumber}, _, {ancestorNumber}) failed");
    }

    [Test]
    public void GetAncestorAt_all_pairs_correct()
    {
        (SkipIndexedBlockInfoStore store, BlockHeader[] headers) = BuildChain(ChainLength);

        for (int block = 0; block < ChainLength; block++)
        {
            for (int ancestor = 0; ancestor <= block; ancestor++)
            {
                ValueHash256 hash = headers[block].Hash!.ValueHash256;
                ValueHash256 expected = headers[ancestor].Hash!.ValueHash256;

                ValueHash256? result = store.GetAncestorAt(block, in hash, ancestor);
                result.Should().Be(expected, $"GetAncestorAt({block}, _, {ancestor}) failed");
            }
        }
    }

    [Test]
    public void GetAncestorAt_returns_null_for_missing_header()
    {
        MemDb headersDb = new();
        MemDb blockNumbersDb = new();
        MemDb cumulativeDb = new();
        HeaderStore headerStore = new(headersDb, blockNumbersDb);
        SkipIndexedBlockInfoStore store = new(cumulativeDb, headerStore, new CumulativeTotalDifficultyStrategy(), NullTotalDifficultyAnchor.Instance, LimboLogs.Instance);

        ValueHash256 fakeHash = new(Keccak.Compute("nonexistent").Bytes);
        ValueHash256? result = store.GetAncestorAt(5, in fakeHash, 0);
        result.Should().BeNull();
    }

    [Test]
    public void Fork_blocks_have_independent_entries()
    {
        MemDb headersDb = new();
        MemDb blockNumbersDb = new();
        MemDb cumulativeDb = new();
        HeaderStore headerStore = new(headersDb, blockNumbersDb);

        BlockHeader genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithDifficulty(100)
            .WithParentHash(Keccak.Zero)
            .TestObject;
        headerStore.Insert(genesis);

        BlockHeader block1A = Build.A.BlockHeader
            .WithNumber(1)
            .WithDifficulty(200)
            .WithParentHash(genesis.Hash!)
            .TestObject;
        headerStore.Insert(block1A);

        BlockHeader block1B = Build.A.BlockHeader
            .WithNumber(1)
            .WithDifficulty(300)
            .WithParentHash(genesis.Hash!)
            .WithExtraData(new byte[] { 0xFF })
            .TestObject;
        headerStore.Insert(block1B);

        SkipIndexedBlockInfoStore store = new(cumulativeDb, headerStore, new CumulativeTotalDifficultyStrategy(), NullTotalDifficultyAnchor.Instance, LimboLogs.Instance);

        ValueHash256 hashA = block1A.Hash!.ValueHash256;
        ValueHash256 hashB = block1B.Hash!.ValueHash256;

        UInt256? tdA = store.GetTotalDifficulty(1, in hashA);
        UInt256? tdB = store.GetTotalDifficulty(1, in hashB);

        tdA.Should().Be((UInt256)300); // 100 + 200
        tdB.Should().Be((UInt256)400); // 100 + 300
    }

    [Test]
    public void Lazy_caching_second_call_returns_same_result()
    {
        (SkipIndexedBlockInfoStore store, BlockHeader[] headers) = BuildChain(8);

        ValueHash256 hash = headers[7].Hash!.ValueHash256;

        UInt256? td1 = store.GetTotalDifficulty(7, in hash);
        UInt256? td2 = store.GetTotalDifficulty(7, in hash);

        td1.Should().Be(td2);
    }

    [Test]
    public void Inner_skip_never_overshoots_target()
    {
        // This test verifies the key invariant from algo.md:
        // for any block B in (targetBlockNumber, blockNumber), B - skipDist(B) >= targetBlockNumber
        // We exercise this by building a longer chain and querying TD for blocks with large skip distances
        (SkipIndexedBlockInfoStore store, BlockHeader[] headers) = BuildChain(64);

        // Blocks with large skipDistance: 32 (skip=32), 16 (skip=16), 48 (skip=16)
        // These trigger the inner traversal loop
        int[] testBlocks = [16, 32, 48, 64 - 1];
        foreach (int block in testBlocks)
        {
            if (block >= 64) continue;
            ValueHash256 hash = headers[block].Hash!.ValueHash256;
            // If the assertion in EnsurePopulated fires, this will throw
            UInt256? td = store.GetTotalDifficulty(block, in hash);
            td.Should().NotBeNull($"TD should be computable for block {block}");
        }
    }

    [Test]
    public void GetTotalDifficulty_1000_block_random_chain()
    {
        const int length = 1000;
        Random rng = new(42);

        MemDb headersDb = new();
        MemDb blockNumbersDb = new();
        HeaderStore headerStore = new(headersDb, blockNumbersDb);

        BlockHeader[] headers = new BlockHeader[length];
        UInt256[] expectedTd = new UInt256[length];

        UInt256 genesisDiff = (UInt256)rng.Next(1, 10000);
        BlockHeader genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithDifficulty(genesisDiff)
            .WithParentHash(Keccak.Zero)
            .TestObject;
        headerStore.Insert(genesis);
        headers[0] = genesis;
        expectedTd[0] = genesisDiff;

        for (int i = 1; i < length; i++)
        {
            UInt256 diff = (UInt256)rng.Next(1, 10000);
            BlockHeader header = Build.A.BlockHeader
                .WithNumber(i)
                .WithDifficulty(diff)
                .WithParentHash(headers[i - 1].Hash!)
                .TestObject;
            headerStore.Insert(header);
            headers[i] = header;
            expectedTd[i] = expectedTd[i - 1] + diff;
        }

        for (int i = 0; i < length; i++)
        {
            SkipIndexedBlockInfoStore store = new(new MemDb(), headerStore, new CumulativeTotalDifficultyStrategy(), NullTotalDifficultyAnchor.Instance, LimboLogs.Instance);
            ValueHash256 hash = headers[i].Hash!.ValueHash256;
            UInt256? td = store.GetTotalDifficulty(i, in hash);
            td.Should().Be(expectedTd[i], $"TD mismatch at block {i}");
        }
    }

    [Test]
    public void GetTotalDifficulty_with_FixedTotalDifficultyStrategy_resets_at_boundary()
    {
        const int preBoundary = 4; // blocks 0..3 accumulate normally
        const int postBoundary = 4; // blocks 4..7 accumulate after reset
        const int length = preBoundary + postBoundary;

        MemDb headersDb = new();
        MemDb blockNumbersDb = new();
        HeaderStore headerStore = new(headersDb, blockNumbersDb);

        BlockHeader[] headers = new BlockHeader[length];
        UInt256 preDifficulty = 10;

        BlockHeader genesis = Build.A.BlockHeader
            .WithNumber(0).WithDifficulty(preDifficulty).WithParentHash(Keccak.Zero).TestObject;
        headerStore.Insert(genesis);
        headers[0] = genesis;

        for (int i = 1; i < preBoundary; i++)
        {
            BlockHeader header = Build.A.BlockHeader
                .WithNumber(i).WithDifficulty(preDifficulty).WithParentHash(headers[i - 1].Hash!).TestObject;
            headerStore.Insert(header);
            headers[i] = header;
        }

        UInt256 terminalTotalDifficulty = preDifficulty * preBoundary;

        for (int i = preBoundary; i < length; i++)
        {
            BlockHeader header = Build.A.BlockHeader
                .WithNumber(i).WithDifficulty(UInt256.Zero).WithParentHash(headers[i - 1].Hash!).TestObject;
            headerStore.Insert(header);
            headers[i] = header;
        }

        FixedTotalDifficultyStrategy strategy = new(
            new CumulativeTotalDifficultyStrategy(),
            fixesBlockNumber: preBoundary - 1,
            toTotalDifficulty: terminalTotalDifficulty);
        SkipIndexedBlockInfoStore store = new(new MemDb(), headerStore, strategy, NullTotalDifficultyAnchor.Instance, LimboLogs.Instance);

        for (int i = 0; i < preBoundary; i++)
        {
            ValueHash256 hash = headers[i].Hash!.ValueHash256;
            UInt256? td = store.GetTotalDifficulty(i, in hash);
            td.Should().Be(preDifficulty * (ulong)(i + 1), $"TD mismatch at pre-reset block {i}");
        }

        for (int i = preBoundary; i < length; i++)
        {
            ValueHash256 hash = headers[i].Hash!.ValueHash256;
            UInt256? td = store.GetTotalDifficulty(i, in hash);
            td.Should().Be(UInt256.Zero, $"TD mismatch at post-reset block {i}");
        }
    }

    private static (SkipIndexedBlockInfoStore store, BlockHeader[] headers) BuildChain(int length)
    {
        MemDb headersDb = new();
        MemDb blockNumbersDb = new();
        MemDb cumulativeDb = new();
        HeaderStore headerStore = new(headersDb, blockNumbersDb);

        BlockHeader[] headers = new BlockHeader[length];

        BlockHeader genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithDifficulty((UInt256)(length * 10))
            .WithParentHash(Keccak.Zero)
            .TestObject;
        headerStore.Insert(genesis);
        headers[0] = genesis;

        for (int i = 1; i < length; i++)
        {
            BlockHeader header = Build.A.BlockHeader
                .WithNumber(i)
                .WithDifficulty((UInt256)(i + 1))
                .WithParentHash(headers[i - 1].Hash!)
                .TestObject;
            headerStore.Insert(header);
            headers[i] = header;
        }

        SkipIndexedBlockInfoStore store = new(cumulativeDb, headerStore, new CumulativeTotalDifficultyStrategy(), NullTotalDifficultyAnchor.Instance, LimboLogs.Instance);
        return (store, headers);
    }

    [TestCase(16, 4)]   // skip from block 5 lands exactly on pivot=4 (SD(5)=1)
    [TestCase(16, 2)]   // skip from block 3 lands exactly on pivot=2 (SD(3)=1)
    [TestCase(16, 1)]   // pivot=1: TD(1) known; TD(2) skip lands on 0 overshoot
    [TestCase(64, 32)]  // pivot=32: near-pivot blocks chain through synth pivot entry
    public void GetTotalDifficulty_with_anchor_returns_correct_td(int chainLength, int pivotNumber)
    {
        BlockHeader[] headers = BuildHeaders(chainLength);
        HeaderStore headerStore = CreateHeaderStoreWith(headers);
        UInt256 pivotTd = ExpectedTd(headers, pivotNumber);
        ITotalDifficultyAnchor anchor = new StubAnchor(new TotalDifficultyAnchor(
            pivotNumber, headers[pivotNumber].Hash!.ValueHash256, pivotTd));

        SkipIndexedBlockInfoStore store = new(new MemDb(), headerStore, new CumulativeTotalDifficultyStrategy(), anchor, LimboLogs.Instance);

        for (int i = pivotNumber; i < chainLength; i++)
        {
            ValueHash256 hash = headers[i].Hash!.ValueHash256;
            UInt256? td = store.GetTotalDifficulty(i, in hash);
            td.Should().Be(ExpectedTd(headers, i), $"TD mismatch at block {i} with anchor at {pivotNumber}");
        }
    }

    [Test]
    public void GetTotalDifficulty_below_anchor_with_headers_computes_normally()
    {
        BlockHeader[] headers = BuildHeaders(16);
        HeaderStore headerStore = CreateHeaderStoreWith(headers);
        ITotalDifficultyAnchor anchor = new StubAnchor(new TotalDifficultyAnchor(
            5, headers[5].Hash!.ValueHash256, ExpectedTd(headers, 5)));

        SkipIndexedBlockInfoStore store = new(new MemDb(), headerStore, new CumulativeTotalDifficultyStrategy(), anchor, LimboLogs.Instance);

        for (int i = 0; i < 5; i++)
        {
            ValueHash256 hash = headers[i].Hash!.ValueHash256;
            store.GetTotalDifficulty(i, in hash).Should().Be(ExpectedTd(headers, i),
                $"below-anchor TD mismatch at block {i}");
        }
    }

    [Test]
    public void GetTotalDifficulty_at_pivot_returns_anchor_td_directly()
    {
        BlockHeader[] headers = BuildHeaders(16);
        HeaderStore headerStore = CreateHeaderStoreWith(headers);
        UInt256 pivotTd = ExpectedTd(headers, 5);
        ITotalDifficultyAnchor anchor = new StubAnchor(new TotalDifficultyAnchor(
            5, headers[5].Hash!.ValueHash256, pivotTd));

        MemDb cumulativeDb = new();
        SkipIndexedBlockInfoStore store = new(cumulativeDb, headerStore, new CumulativeTotalDifficultyStrategy(), anchor, LimboLogs.Instance);

        ValueHash256 pivotHash = headers[5].Hash!.ValueHash256;
        store.GetTotalDifficulty(5, in pivotHash).Should().Be(pivotTd);
        cumulativeDb.WritesCount.Should().Be(0, "pivot block must not persist a synthetic entry");
    }

    [Test]
    public void GetTotalDifficulty_pivot_hash_mismatch_returns_null()
    {
        BlockHeader[] headers = BuildHeaders(16);
        HeaderStore headerStore = CreateHeaderStoreWith(headers);
        ValueHash256 wrongHash = new(Keccak.Compute("wrong-pivot-hash").Bytes);
        ITotalDifficultyAnchor anchor = new StubAnchor(new TotalDifficultyAnchor(
            5, wrongHash, ExpectedTd(headers, 5)));

        SkipIndexedBlockInfoStore store = new(new MemDb(), headerStore, new CumulativeTotalDifficultyStrategy(), anchor, LimboLogs.Instance);

        // Query at pivot with real hash — should fail because anchor hash differs.
        ValueHash256 realPivotHash = headers[5].Hash!.ValueHash256;
        store.GetTotalDifficulty(5, in realPivotHash).Should().BeNull();

        // Query above pivot — chain walks to pivot, hash mismatch detected.
        ValueHash256 aboveHash = headers[6].Hash!.ValueHash256;
        store.GetTotalDifficulty(6, in aboveHash).Should().BeNull();
    }

    [TestCase(16, 5, 5)]   // ancestor at pivot
    [TestCase(16, 5, 10)]  // ancestor above pivot
    [TestCase(16, 5, 15)]  // ancestor at head
    public void GetAncestorAt_with_anchor_returns_correct_ancestor(int chainLength, int pivotNumber, int blockNumber)
    {
        BlockHeader[] headers = BuildHeaders(chainLength);
        HeaderStore headerStore = CreateHeaderStoreWith(headers);
        ITotalDifficultyAnchor anchor = new StubAnchor(new TotalDifficultyAnchor(
            pivotNumber, headers[pivotNumber].Hash!.ValueHash256, ExpectedTd(headers, pivotNumber)));

        SkipIndexedBlockInfoStore store = new(new MemDb(), headerStore, new CumulativeTotalDifficultyStrategy(), anchor, LimboLogs.Instance);

        ValueHash256 blockHash = headers[blockNumber].Hash!.ValueHash256;
        for (int target = pivotNumber; target <= blockNumber; target++)
        {
            ValueHash256? got = store.GetAncestorAt(blockNumber, in blockHash, target);
            got.Should().Be(headers[target].Hash!.ValueHash256,
                $"GetAncestorAt({blockNumber}, _, {target}) with anchor at {pivotNumber}");
        }
    }

    [Test]
    public void GetAncestorAt_below_anchor_with_headers_returns_correct_ancestor()
    {
        BlockHeader[] headers = BuildHeaders(16);
        HeaderStore headerStore = CreateHeaderStoreWith(headers);
        ITotalDifficultyAnchor anchor = new StubAnchor(new TotalDifficultyAnchor(
            5, headers[5].Hash!.ValueHash256, ExpectedTd(headers, 5)));

        SkipIndexedBlockInfoStore store = new(new MemDb(), headerStore, new CumulativeTotalDifficultyStrategy(), anchor, LimboLogs.Instance);

        ValueHash256 headHash = headers[10].Hash!.ValueHash256;
        store.GetAncestorAt(10, in headHash, 3).Should().Be(headers[3].Hash!.ValueHash256);
    }

    [Test]
    public void GetTotalDifficulty_fast_sync_only_post_pivot_headers()
    {
        // Simulates a fast-sync node: headers only exist from pivot onward. Every query above the
        // pivot should resolve correctly because the skip-list build uses GetHeaderInfo at the
        // target block (no recursion below it), so pre-pivot headers are never required.
        const int chainLength = 16;
        const int pivotNumber = 5;
        BlockHeader[] headers = BuildHeaders(chainLength);

        MemDb headersDb = new();
        MemDb blockNumbersDb = new();
        HeaderStore headerStore = new(headersDb, blockNumbersDb);
        for (int i = pivotNumber; i < chainLength; i++) headerStore.Insert(headers[i]);

        UInt256 pivotTd = ExpectedTd(headers, pivotNumber);
        ITotalDifficultyAnchor anchor = new StubAnchor(new TotalDifficultyAnchor(
            pivotNumber, headers[pivotNumber].Hash!.ValueHash256, pivotTd));

        SkipIndexedBlockInfoStore store = new(new MemDb(), headerStore, new CumulativeTotalDifficultyStrategy(), anchor, LimboLogs.Instance);

        // Fast path (anchor itself).
        store.GetTotalDifficulty(pivotNumber, in headers[pivotNumber].Hash!.ValueHash256)
            .Should().Be(pivotTd);

        // All above-anchor queries should resolve to the expected TD without touching pre-anchor headers.
        for (int i = pivotNumber + 1; i < chainLength; i++)
        {
            store.GetTotalDifficulty(i, in headers[i].Hash!.ValueHash256)
                .Should().Be(ExpectedTd(headers, i), $"post-pivot TD mismatch at block {i}");
        }

        // Below-anchor queries fail — those headers aren't available.
        ValueHash256 fakeBelow = new(Keccak.Compute("pre-pivot").Bytes);
        store.GetTotalDifficulty(pivotNumber - 1, in fakeBelow).Should().BeNull();
    }

    // upper/lower pairs that exercise both boundaries: skip-aligned targets, interior stops, the
    // one-step and whole-range cases, and odd-bit patterns.
    [TestCase(96, 64)]   // upper = skip parent's block, natural skip covers whole range
    [TestCase(96, 80)]   // stop inside the 96->64 skip range
    [TestCase(96, 95)]   // single-step walk
    [TestCase(96, 0)]    // all the way to genesis
    [TestCase(128, 0)]   // SkipDistance(128) == 128 so target == 0 directly
    [TestCase(128, 64)]  // stop halfway into 128's own skip range
    [TestCase(64, 0)]    // target == 0 at first jump
    [TestCase(100, 96)]  // small step, odd bit pattern
    [TestCase(100, 50)]  // interior stop
    [TestCase(255, 128)] // dense-bit upper
    [TestCase(255, 0)]
    [TestCase(1, 0)]     // minimal walk
    public void GetAncestorAt_only_fetches_headers_within_range(int upper, int lower)
    {
        // For a walk from `upper` down to `lower` the store must never request a header for any
        // block outside [lower, upper]. The header store only has entries for that range; if any
        // call escapes the bound the recorded set will include it, and the assertion fails.
        BlockHeader[] headers = BuildHeaders(upper + 1);
        RecordingHeaderStore headerStore = new();
        for (int i = lower; i <= upper; i++) headerStore.Insert(headers[i]);

        SkipIndexedBlockInfoStore store = new(new MemDb(), headerStore, new CumulativeTotalDifficultyStrategy(), NullTotalDifficultyAnchor.Instance, LimboLogs.Instance);

        ValueHash256? ancestor = store.GetAncestorAt(upper, in headers[upper].Hash!.ValueHash256, lower);
        ancestor.Should().Be(headers[lower].Hash!.ValueHash256, $"ancestor lookup failed for ({upper}, {lower})");

        headerStore.RequestedBlockNumbers.Should().OnlyContain(n => n >= lower && n <= upper,
            $"out-of-range header fetch for ({upper}, {lower}): [{string.Join(", ", headerStore.RequestedBlockNumbers)}]");
    }

    private static BlockHeader[] BuildHeaders(int length)
    {
        BlockHeader[] headers = new BlockHeader[length];
        BlockHeader genesis = Build.A.BlockHeader
            .WithNumber(0)
            .WithDifficulty((UInt256)(length * 10))
            .WithParentHash(Keccak.Zero)
            .TestObject;
        headers[0] = genesis;
        for (int i = 1; i < length; i++)
        {
            headers[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithDifficulty((UInt256)(i + 1))
                .WithParentHash(headers[i - 1].Hash!)
                .TestObject;
        }
        return headers;
    }

    private static HeaderStore CreateHeaderStoreWith(BlockHeader[] headers)
    {
        MemDb headersDb = new();
        MemDb blockNumbersDb = new();
        HeaderStore store = new(headersDb, blockNumbersDb);
        foreach (BlockHeader h in headers) store.Insert(h);
        return store;
    }

    private static UInt256 ExpectedTd(BlockHeader[] headers, int index)
    {
        UInt256 td = UInt256.Zero;
        for (int i = 0; i <= index; i++) td += headers[i].Difficulty;
        return td;
    }

    private sealed class StubAnchor(TotalDifficultyAnchor anchor) : ITotalDifficultyAnchor
    {
        public TotalDifficultyAnchor? TryGet() => anchor;
    }

    private sealed class RecordingHeaderStore : IHeaderStore
    {
        private readonly HeaderStore _inner = new(new MemDb(), new MemDb());
        public HashSet<long> RequestedBlockNumbers { get; } = [];

        public void Insert(BlockHeader header) => _inner.Insert(header);
        public void BulkInsert(IReadOnlyList<BlockHeader> headers) => _inner.BulkInsert(headers);

        public BlockHeader? Get(Hash256 blockHash, bool shouldCache, long? blockNumber = null)
        {
            if (blockNumber.HasValue) RequestedBlockNumbers.Add(blockNumber.Value);
            return _inner.Get(blockHash, shouldCache, blockNumber);
        }

        public BlockHeader? Get(Hash256 blockHash, long? blockNumber = null) =>
            Get(blockHash, true, blockNumber);

        public void Cache(BlockHeader header) => _inner.Cache(header);
        public void Delete(Hash256 blockHash) => _inner.Delete(blockHash);
        public void InsertBlockNumber(Hash256 blockHash, long blockNumber) => _inner.InsertBlockNumber(blockHash, blockNumber);
        public long? GetBlockNumber(Hash256 blockHash) => _inner.GetBlockNumber(blockHash);
        public Core.Collections.IOwnedReadOnlyList<BlockHeader> FindReversedHeaders(long endBlockNumber, Hash256 endBlockHash, int count) =>
            _inner.FindReversedHeaders(endBlockNumber, endBlockHash, count);
    }
}
