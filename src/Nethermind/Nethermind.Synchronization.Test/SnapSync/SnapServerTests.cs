// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.SnapServer;
using Nethermind.Synchronization.SnapSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapServerTests
{
    private TestMemDb _codeDb = null!;
    private ReadCountingDb _balDb = null!;
    private HeaderStore _headerStore = null!;
    private BlockAccessListStore _balStore = null!;
    private SnapServer _server = null!;

    [SetUp]
    public void SetUp()
    {
        _codeDb = new TestMemDb();
        _balDb = new ReadCountingDb();
        _headerStore = new HeaderStore(new TestMemDb(), new TestMemDb());
        _balStore = new BlockAccessListStore(_balDb);
        _server = new SnapServer(NoopSnapServer.Instance, _codeDb, _headerStore, _balStore);
    }

    [TearDown]
    public void TearDown()
    {
        _codeDb.Dispose();
        _balDb.Dispose();
    }

    private ValueHash256 StoreCode(byte[] code)
    {
        Hash256 hash = Keccak.Compute(code);
        _codeDb[hash.Bytes] = code;
        return hash.ValueHash256;
    }

    private void GivenBlock(Hash256 hash, ulong number, byte[]? bal)
    {
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(number)
            .WithBlockAccessListHash(bal is null ? null : TestItem.KeccakA)
            .WithHash(hash)
            .TestObject;
        _headerStore.Insert(header);
        if (bal is not null) _balStore.Insert(number, hash, bal);
    }

    [Test]
    public void GetByteCodes_returns_requested_codes_in_order()
    {
        byte[] codeA = [1, 2, 3];
        byte[] codeB = [4, 5, 6, 7];
        ValueHash256 hashA = StoreCode(codeA);
        ValueHash256 hashB = StoreCode(codeB);

        using IByteArrayList result = _server.GetByteCodes([hashA, hashB], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0], Is.SequenceEqualTo(codeA));
        Assert.That(result[1], Is.SequenceEqualTo(codeB));
        // Peers ask for hash-random code, so serving must not churn the block cache.
        _codeDb.KeyWasReadWithFlags(hashA.ToByteArray(), ReadFlags.HintCacheMiss);
        _codeDb.KeyWasReadWithFlags(hashB.ToByteArray(), ReadFlags.HintCacheMiss);
    }

    [Test]
    public void GetByteCodes_empty_string_hash_returns_empty_entry()
    {
        using IByteArrayList result = _server.GetByteCodes(
            [Keccak.OfAnEmptyString.ValueHash256], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Length, Is.EqualTo(0));
    }

    [Test]
    public void GetByteCodes_skips_missing_code()
    {
        byte[] code = [1, 2, 3];
        ValueHash256 present = StoreCode(code);
        ValueHash256 missing = Keccak.Compute([9, 9, 9]).ValueHash256;

        using IByteArrayList result = _server.GetByteCodes([missing, present], long.MaxValue, CancellationToken.None);

        // The missing hash contributes no entry, so only the present code is returned.
        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0], Is.SequenceEqualTo(code));
    }

    [Test]
    public void GetByteCodes_respects_byte_limit()
    {
        ValueHash256 hashA = StoreCode(new byte[100]);
        ValueHash256 hashB = StoreCode(new byte[100]);

        // A byte limit smaller than the first code stops the loop after a single entry is written.
        using IByteArrayList result = _server.GetByteCodes([hashA, hashB], 1, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(1));
    }

    // Code is read as native memory the store must free, so a missing code or a read cut short by the limit still frees it.
    [Test]
    public void GetByteCodes_releases_every_code_it_reads()
    {
        ValueHash256 hashA = StoreCode(new byte[100]);
        ValueHash256 hashB = StoreCode(new byte[100]);
        ValueHash256 missing = Keccak.Compute([9, 9, 9]).ValueHash256;
        ReleaseCountingCodeDb codeDb = new(_codeDb);
        SnapServer server = new(NoopSnapServer.Instance, codeDb, _headerStore, _balStore);

        using IByteArrayList result = server.GetByteCodes([missing, hashA, hashB], 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(codeDb.Reads, Is.EqualTo(2), "the limit stops the reads after the first code");
            Assert.That(codeDb.Held, Is.Zero);
        }
    }

    /// <summary>Counts code reads and the ones not yet released.</summary>
    private sealed class ReleaseCountingCodeDb(IReadOnlyKeyValueStore inner) : IReadOnlyKeyValueStore
    {
        public int Reads { get; private set; }
        public int Held { get; private set; }

        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => inner.Get(key, flags);

        public Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            Reads++;
            Held++;
            return inner.GetSpan(key, flags);
        }

        public void DangerousReleaseMemory(in ReadOnlySpan<byte> span)
        {
            Held--;
            inner.DangerousReleaseMemory(span);
        }
    }

    [Test]
    public void GetByteCodes_keeps_request_order_across_read_chunks([Values] bool batching)
    {
        List<byte[]> expected = [];
        List<ValueHash256> request = [];
        for (int i = 0; i < 150; i++)
        {
            if (i % 10 == 3)
            {
                request.Add(Keccak.Compute(BitConverter.GetBytes(-i)).ValueHash256);
            }
            else if (i == 70)
            {
                request.Add(Keccak.OfAnEmptyString.ValueHash256);
                expected.Add([]);
            }
            else
            {
                byte[] code = BitConverter.GetBytes(i);
                request.Add(StoreCode(code));
                expected.Add(code);
            }
        }

        using IByteArrayList result = ServerOver(batching).GetByteCodes(request, long.MaxValue, CancellationToken.None);

        AssertCodes(result, expected);
        _codeDb.KeyWasReadWithFlags(request[0].ToByteArray(), ReadFlags.HintCacheMiss);
    }

    [Test]
    public void GetByteCodes_stops_after_the_code_that_passes_the_byte_limit([Values] bool batching)
    {
        ValueHash256 hashA = StoreCode(Enumerable.Repeat((byte)1, 100).ToArray());
        ValueHash256 hashB = StoreCode(Enumerable.Repeat((byte)2, 100).ToArray());
        ValueHash256 hashC = StoreCode(Enumerable.Repeat((byte)3, 100).ToArray());

        using IByteArrayList result = ServerOver(batching).GetByteCodes(
            [hashA, hashB, Keccak.OfAnEmptyString.ValueHash256, hashC], 150, CancellationToken.None);

        AssertCodes(result, [_codeDb[hashA.Bytes]!, _codeDb[hashB.Bytes]!]);
    }

    [TestCase(1, 0, 1, 1)]
    [TestCase(500, 0, 51, 63)]
    [TestCase(66, 6, 13, 8, TestName = "Empty codes that flush part chunks do not grow the chunk")]
    public void GetByteCodes_reads_little_past_the_byte_limit(long byteLimit, int emptyCodePairs, int served, int reads)
    {
        List<ValueHash256> request = [];
        for (int i = 0; i < 100; i++)
        {
            request.Add(StoreCode([.. BitConverter.GetBytes(i), 0, 0, 0, 0, 0, 0]));
            if (i < emptyCodePairs) request.Add(Keccak.OfAnEmptyString.ValueHash256);
        }
        BatchReadingCodeDb codeDb = new(_codeDb);

        using IByteArrayList result = new SnapServer(NoopSnapServer.Instance, codeDb, _headerStore, _balStore)
            .GetByteCodes(request, byteLimit, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Count, Is.EqualTo(served));
            Assert.That(codeDb.Reads, Is.EqualTo(reads));
        }
    }

    [Test]
    public void GetByteCodes_serves_nothing_when_cancelled([Values] bool batching)
    {
        ValueHash256 hash = StoreCode([1, 2, 3]);

        using IByteArrayList result = ServerOver(batching).GetByteCodes(
            [hash, Keccak.OfAnEmptyString.ValueHash256], long.MaxValue, new CancellationToken(canceled: true));

        Assert.That(result.Count, Is.Zero);
    }

    private SnapServer ServerOver(bool batching) =>
        new(NoopSnapServer.Instance, batching ? new BatchReadingCodeDb(_codeDb) : _codeDb, _headerStore, _balStore);

    private static void AssertCodes(IByteArrayList result, IReadOnlyList<byte[]> expected)
    {
        Assert.That(result.Count, Is.EqualTo(expected.Count));
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.That(result[i], Is.SequenceEqualTo(expected[i]), $"code {i}");
            }
        }
    }

    /// <summary>Serves batch reads from a memory database, as RocksDB does, without calling the decoder for missing keys.</summary>
    private sealed class BatchReadingCodeDb(IReadOnlyKeyValueStore inner) : IReadOnlyKeyValueStore, IBatchReadOnlyDb
    {
        public int Reads { get; private set; }

        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
            throw new InvalidOperationException("Codes must be read in batches.");

        public void GetMany<T, TDecoder>(ReadOnlySpan<byte> keys, int keyLength, Span<T> values, TDecoder decoder, ReadFlags flags)
            where TDecoder : IValueDecoder<T>
        {
            Reads += values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                byte[]? value = inner.Get(keys.Slice(i * keyLength, keyLength), flags);
                values[i] = value is null ? default! : decoder.Decode(value);
            }
        }
    }

    [Test]
    public void GetBlockAccessLists_returns_rlp_for_known_blocks()
    {
        byte[] balA = [1, 2, 3];
        byte[] balB = [4, 5, 6, 7];
        GivenBlock(TestItem.KeccakA, 1, balA);
        GivenBlock(TestItem.KeccakB, 2, balB);

        using IByteArrayList result = _server.GetBlockAccessLists(
            [TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0], Is.SequenceEqualTo(balA));
        Assert.That(result[1], Is.SequenceEqualTo(balB));
    }

    [Test]
    public void GetBlockAccessLists_returns_empty_entry_for_unknown_block()
    {
        byte[] bal = [1, 2, 3];
        GivenBlock(TestItem.KeccakA, 1, bal);

        using IByteArrayList result = _server.GetBlockAccessLists(
            [TestItem.KeccakB.ValueHash256, TestItem.KeccakA.ValueHash256], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0].Length, Is.EqualTo(0));
        Assert.That(result[1], Is.SequenceEqualTo(bal));
    }

    [Test]
    public void GetBlockAccessLists_returns_empty_entry_for_block_without_stored_list()
    {
        byte[] bal = [1, 2, 3];
        GivenBlock(TestItem.KeccakA, 1, bal);
        // Header exists but no BAL is stored for it.
        GivenBlock(TestItem.KeccakB, 2, null);

        using IByteArrayList result = _server.GetBlockAccessLists(
            [TestItem.KeccakB.ValueHash256, TestItem.KeccakA.ValueHash256], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0].Length, Is.EqualTo(0));
        Assert.That(result[1], Is.SequenceEqualTo(bal));
    }

    [Test]
    public void GetBlockAccessLists_keeps_request_order_across_read_chunks()
    {
        List<ValueHash256> request = [];
        List<byte[]> expected = [];
        for (int i = 0; i < 40; i++)
        {
            Hash256 hash = Keccak.Compute(BitConverter.GetBytes(i));
            byte[]? bal = i % 7 == 2 ? null : [0xc1, (byte)i];
            if (i % 5 != 4) GivenBlock(hash, (ulong)i, bal);
            request.Add(hash.ValueHash256);
            expected.Add(i % 5 == 4 ? [] : bal ?? []);
        }

        using IByteArrayList result = _server.GetBlockAccessLists(request, long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(expected.Count));
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.That(result[i], Is.SequenceEqualTo(expected[i]), $"block {i}");
            }
        }
    }

    [TestCase(1, 0, 1, 1)]
    [TestCase(150, 0, 16, 31)]
    [TestCase(15, 15, 16, 1, Description = "Unknown hashes read no list, so they do not grow the batch")]
    public void GetBlockAccessLists_reads_little_past_the_byte_limit(long byteLimit, int unknownHashes, int served, int maxReads)
    {
        List<ValueHash256> request = [];
        for (int i = 0; i < unknownHashes; i++)
        {
            request.Add(Keccak.Compute(BitConverter.GetBytes(-1 - i)).ValueHash256);
        }

        for (int i = 0; i < 40; i++)
        {
            Hash256 hash = Keccak.Compute(BitConverter.GetBytes(i));
            GivenBlock(hash, (ulong)i, new byte[10]);
            request.Add(hash.ValueHash256);
        }

        using IByteArrayList result = _server.GetBlockAccessLists(request, byteLimit, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Count, Is.EqualTo(served));
            Assert.That(_balDb.Reads, Is.InRange(served - unknownHashes, maxReads));
        }
    }

    private sealed class ReadCountingDb : TestMemDb
    {
        public int Reads { get; private set; }

        public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            Reads++;
            return base.Get(key, flags);
        }
    }

    [Test]
    public void GetBlockAccessLists_stops_after_the_list_that_passes_the_byte_limit()
    {
        GivenBlock(TestItem.KeccakA, 1, new byte[100]);
        GivenBlock(TestItem.KeccakB, 2, new byte[100]);
        GivenBlock(TestItem.KeccakC, 3, new byte[100]);

        using IByteArrayList result = _server.GetBlockAccessLists(
            [TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256, TestItem.KeccakD.ValueHash256, TestItem.KeccakC.ValueHash256],
            150, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(2));
    }

    [Test]
    public void GetBlockAccessLists_respects_byte_limit()
    {
        GivenBlock(TestItem.KeccakA, 1, new byte[100]);
        GivenBlock(TestItem.KeccakB, 2, new byte[100]);

        // A byte limit below the first list stops the loop after a single entry is written.
        using IByteArrayList result = _server.GetBlockAccessLists(
            [TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256], 1, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(1));
    }
}
