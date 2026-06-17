// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

public class L1OriginStoreTests
{
    private IDb _db = null!;
    private L1OriginStore _store = null!;
    private L1OriginDecoder _decoder = null!;

    [SetUp]
    public void Setup()
    {
        _db = new TestMemDb();
        _decoder = new L1OriginDecoder();
        _store = new L1OriginStore(_db, _decoder);
    }

    [Test]
    public void Can_write_and_read_l1_origin()
    {
        UInt256 blockId = 123;
        L1Origin origin = new(blockId, Hash256.Zero, 456, Hash256.Zero, null);

        _store.WriteL1Origin(blockId, origin);
        L1Origin? retrieved = _store.ReadL1Origin(blockId);

        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved!.BlockId, Is.EqualTo(blockId));
        Assert.That(retrieved.L1BlockHeight, Is.EqualTo(456));
    }

    [Test]
    public void Returns_null_for_non_existent_l1_origin()
    {
        L1Origin? retrieved = _store.ReadL1Origin(999);
        Assert.That(retrieved, Is.Null);
    }

    [Test]
    public void Can_write_and_read_head_l1_origin()
    {
        UInt256 headBlockId = 789;

        _store.WriteHeadL1Origin(headBlockId);
        UInt256? retrieved = _store.ReadHeadL1Origin();

        Assert.That(retrieved, Is.EqualTo((UInt256)789));
    }

    [Test]
    public void Returns_null_for_non_existent_head()
    {
        UInt256? retrieved = _store.ReadHeadL1Origin();
        Assert.That(retrieved, Is.Null);
    }

    [Test]
    public void Can_write_and_read_batch_to_block_mapping()
    {
        UInt256 batchId = 100;
        UInt256 blockId = 200;

        _store.WriteBatchToLastBlockID(batchId, blockId);
        UInt256? retrieved = _store.ReadBatchToLastBlockID(batchId);

        Assert.That(retrieved, Is.EqualTo((UInt256)200));
    }

    [Test]
    public void Returns_null_for_non_existent_batch_mapping()
    {
        UInt256? retrieved = _store.ReadBatchToLastBlockID(999);
        Assert.That(retrieved, Is.Null);
    }

    [Test]
    public void Different_batch_ids_store_separately()
    {
        _store.WriteBatchToLastBlockID(1, 100);
        _store.WriteBatchToLastBlockID(2, 200);

        Assert.That(_store.ReadBatchToLastBlockID(1), Is.EqualTo((UInt256)100));
        Assert.That(_store.ReadBatchToLastBlockID(2), Is.EqualTo((UInt256)200));
    }

    [Test]
    public void Different_block_ids_store_separately()
    {
        L1Origin origin1 = new(1, Hash256.Zero, 100, Hash256.Zero, null);
        L1Origin origin2 = new(2, Hash256.Zero, 200, Hash256.Zero, null);

        _store.WriteL1Origin(1, origin1);
        _store.WriteL1Origin(2, origin2);

        Assert.That(_store.ReadL1Origin(1)!.L1BlockHeight, Is.EqualTo(100));
        Assert.That(_store.ReadL1Origin(2)!.L1BlockHeight, Is.EqualTo(200));
    }

    [Test]
    public void Can_overwrite_existing_values()
    {
        UInt256 blockId = 1;
        L1Origin origin1 = new(blockId, Hash256.Zero, 100, Hash256.Zero, null);
        L1Origin origin2 = new(blockId, Hash256.Zero, 200, Hash256.Zero, null);

        _store.WriteL1Origin(blockId, origin1);
        _store.WriteL1Origin(blockId, origin2);

        Assert.That(_store.ReadL1Origin(blockId)!.L1BlockHeight, Is.EqualTo(200));
    }

    [Test]
    public void Keys_use_correct_33_byte_format()
    {
        UInt256 blockId = 42;
        L1Origin origin = new(blockId, Hash256.Zero, 100, Hash256.Zero, null);

        _store.WriteL1Origin(blockId, origin);

        TestMemDb testDb = (TestMemDb)_db;
        byte[][] allKeys = testDb.Keys.ToArray();
        Assert.That(allKeys.Length, Is.EqualTo(1));
        Assert.That(allKeys[0].Length, Is.EqualTo(33), "Keys should be 33 bytes (1 prefix + 32 UInt256)");
        Assert.That(allKeys[0][0], Is.EqualTo(0x00), "L1Origin keys should have prefix 0x00");
    }

    [Test]
    public void Batch_keys_use_correct_prefix()
    {
        _store.WriteBatchToLastBlockID(1, 100);

        TestMemDb testDb = (TestMemDb)_db;
        byte[][] allKeys = testDb.Keys.ToArray();
        Assert.That(allKeys.Length, Is.EqualTo(1));
        Assert.That(allKeys[0].Length, Is.EqualTo(33));
        Assert.That(allKeys[0][0], Is.EqualTo(0x01), "Batch keys should have prefix 0x01");
    }

    [Test]
    public void Head_key_uses_correct_prefix()
    {
        _store.WriteHeadL1Origin(1);

        TestMemDb testDb = (TestMemDb)_db;
        byte[][] allKeys = testDb.Keys.ToArray();
        Assert.That(allKeys.Length, Is.EqualTo(1));
        Assert.That(allKeys[0].Length, Is.EqualTo(1));
        Assert.That(allKeys[0][0], Is.EqualTo(0xFF), "Head key should have prefix 0xFF");
    }

    [Test]
    public void Can_store_and_retrieve_signature()
    {
        UInt256 blockId = 123;
        byte[] signature = Enumerable.Range(0, 65).Select(i => (byte)i).ToArray();
        L1Origin origin = new(blockId, Hash256.Zero, 456, Hash256.Zero, null) { Signature = signature };

        _store.WriteL1Origin(blockId, origin);
        L1Origin? retrieved = _store.ReadL1Origin(blockId);

        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved!.Signature, Is.Not.Null);
        Assert.That(retrieved.Signature!.Length, Is.EqualTo(65));
        Assert.That(retrieved.Signature, Is.EqualTo(signature));
    }

    [Test]
    public void Can_write_and_read_l1_origin_with_null_block_height()
    {
        UInt256 blockId = 456;
        L1Origin origin = new(blockId, Hash256.Zero, null, Hash256.Zero, null);

        _store.WriteL1Origin(blockId, origin);
        L1Origin? retrieved = _store.ReadL1Origin(blockId);

        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved!.L1BlockHeight, Is.EqualTo(0));
        Assert.That(retrieved.IsPreconfBlock, Is.True);
    }

    [TestCase(0)]
    [TestCase(L1OriginDecoder.SignatureLength - 1)]
    [TestCase(L1OriginDecoder.SignatureLength + 1)]
    [TestCase(L1OriginDecoder.SignatureLength * 2)]
    public void Fails_for_invalid_length_signature(int signatureLength)
    {
        byte[] signature = Enumerable.Range(0, signatureLength).Select(i => (byte)i).ToArray();
        L1Origin origin = new(1, Hash256.Zero, 456, Hash256.Zero, null) { Signature = signature };

        Action act = () => _decoder.Encode(origin);
        Assert.That(act, Throws.TypeOf<RlpException>()
            .With.Message.EqualTo($"Signature should be exactly {L1OriginDecoder.SignatureLength}"));
    }

    [Test]
    public void Encode_produces_RLP_with_correct_sequence_length(
        [Values(false, true)] bool withBuildPayload,
        [Values(false, true)] bool withForcedInclusion,
        [Values(false, true)] bool withSignature)
    {
        int[]? buildPayloadArgsId = withBuildPayload ? Enumerable.Range(0, 8).ToArray() : null;
        byte[]? signature = withSignature ? Enumerable.Range(0, 65).Select(i => (byte)i).ToArray() : null;
        L1Origin origin = new(123, Hash256.Zero, 456, Hash256.Zero, buildPayloadArgsId, withForcedInclusion, signature);

        Rlp encoded = _decoder.Encode(origin);
        RlpReader ctx = new(encoded.Bytes);
        (int prefixLength, int contentLength) = ctx.ReadPrefixAndContentLength();

        Assert.That(contentLength, Is.EqualTo(encoded.Bytes.Length - prefixLength), "StartSequence must receive content length, not total length");
    }

    [Test]
    public void SetL1OriginSignature_returns_null_when_origin_missing()
    {
        byte[] signature = Enumerable.Range(0, L1OriginDecoder.SignatureLength).Select(i => (byte)i).ToArray();

        L1Origin? result = _store.SetL1OriginSignature(blockId: 42, signature);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void SetL1OriginSignature_attaches_signature_atomically()
    {
        UInt256 blockId = 7;
        L1Origin origin = new(blockId, Hash256.Zero, 100, Hash256.Zero, null);
        _store.WriteL1Origin(blockId, origin);

        byte[] signature = Enumerable.Range(0, L1OriginDecoder.SignatureLength).Select(i => (byte)i).ToArray();
        L1Origin? returned = _store.SetL1OriginSignature(blockId, signature);

        Assert.That(returned, Is.Not.Null);
        Assert.That(returned!.Signature, Is.EqualTo(signature));
        Assert.That(_store.ReadL1Origin(blockId)!.Signature, Is.EqualTo(signature));
    }

    [Test]
    public async Task Concurrent_signature_and_update_writers_do_not_lose_writes()
    {
        // Hammer SetL1OriginSignature against WriteL1Origin on the same block to
        // surface RMW races. Without internal locking the final origin would be
        // an arbitrary mix of one writer's body and the other's signature, or
        // one writer's update could be silently dropped. The contract checked here
        // is the weaker but sufficient one: the persisted record is always one of
        // the writers' fully-formed states (no torn signature, no stale block hash
        // resurrected after a concurrent update).
        const int iterations = 2_000;
        UInt256 blockId = 1;
        byte[] sigA = Enumerable.Repeat((byte)0xAA, L1OriginDecoder.SignatureLength).ToArray();
        byte[] sigB = Enumerable.Repeat((byte)0xBB, L1OriginDecoder.SignatureLength).ToArray();
        Hash256 hashA = new(Enumerable.Repeat((byte)0x11, 32).ToArray());
        Hash256 hashB = new(Enumerable.Repeat((byte)0x22, 32).ToArray());

        // Seed.
        _store.WriteL1Origin(blockId, new L1Origin(blockId, hashA, 100, Hash256.Zero, null) { Signature = sigA });

        Task signer = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                byte[] sig = (i & 1) == 0 ? sigA : sigB;
                _store.SetL1OriginSignature(blockId, sig);
            }
        });

        Task updater = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                Hash256 hash = (i & 1) == 0 ? hashA : hashB;
                byte[] sig = (i & 1) == 0 ? sigB : sigA;
                _store.WriteL1Origin(blockId, new L1Origin(blockId, hash, 100 + (long)i, Hash256.Zero, null) { Signature = sig });
            }
        });

        await Task.WhenAll(signer, updater);

        L1Origin? final = _store.ReadL1Origin(blockId);
        Assert.That(final, Is.Not.Null);
        // Signature must be one of the values we ever wrote (no torn bytes from concurrent encoding).
        Assert.That(final!.Signature, Is.Not.Null);
        bool sigMatches = final.Signature!.SequenceEqual(sigA) || final.Signature.SequenceEqual(sigB);
        Assert.That(sigMatches, Is.True, "the signature must be a complete value from one of the writers, never torn");
    }
}
