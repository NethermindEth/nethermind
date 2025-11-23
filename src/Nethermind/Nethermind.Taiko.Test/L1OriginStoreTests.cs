// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Int256;
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

        retrieved.Should().NotBeNull();
        retrieved!.BlockId.Should().Be(blockId);
        retrieved.L1BlockHeight.Should().Be(456);
    }

    [Test]
    public void Returns_null_for_non_existent_l1_origin()
    {
        L1Origin? retrieved = _store.ReadL1Origin(999);
        retrieved.Should().BeNull();
    }

    [Test]
    public void Can_write_and_read_head_l1_origin()
    {
        UInt256 headBlockId = 789;

        _store.WriteHeadL1Origin(headBlockId);
        UInt256? retrieved = _store.ReadHeadL1Origin();

        retrieved.Should().Be((UInt256)789);
    }

    [Test]
    public void Returns_null_for_non_existent_head()
    {
        UInt256? retrieved = _store.ReadHeadL1Origin();
        retrieved.Should().BeNull();
    }

    [Test]
    public void Can_write_and_read_batch_to_block_mapping()
    {
        UInt256 batchId = 100;
        UInt256 blockId = 200;

        _store.WriteBatchToLastBlockID(batchId, blockId);
        UInt256? retrieved = _store.ReadBatchToLastBlockID(batchId);

        retrieved.Should().Be((UInt256)200);
    }

    [Test]
    public void Returns_null_for_non_existent_batch_mapping()
    {
        UInt256? retrieved = _store.ReadBatchToLastBlockID(999);
        retrieved.Should().BeNull();
    }

    [Test]
    public void Different_batch_ids_store_separately()
    {
        _store.WriteBatchToLastBlockID(1, 100);
        _store.WriteBatchToLastBlockID(2, 200);

        _store.ReadBatchToLastBlockID(1).Should().Be((UInt256)100);
        _store.ReadBatchToLastBlockID(2).Should().Be((UInt256)200);
    }

    [Test]
    public void Different_block_ids_store_separately()
    {
        L1Origin origin1 = new(1, Hash256.Zero, 100, Hash256.Zero, null);
        L1Origin origin2 = new(2, Hash256.Zero, 200, Hash256.Zero, null);

        _store.WriteL1Origin(1, origin1);
        _store.WriteL1Origin(2, origin2);

        _store.ReadL1Origin(1)!.L1BlockHeight.Should().Be(100);
        _store.ReadL1Origin(2)!.L1BlockHeight.Should().Be(200);
    }

    [Test]
    public void Can_overwrite_existing_values()
    {
        UInt256 blockId = 1;
        L1Origin origin1 = new(blockId, Hash256.Zero, 100, Hash256.Zero, null);
        L1Origin origin2 = new(blockId, Hash256.Zero, 200, Hash256.Zero, null);

        _store.WriteL1Origin(blockId, origin1);
        _store.WriteL1Origin(blockId, origin2);

        _store.ReadL1Origin(blockId)!.L1BlockHeight.Should().Be(200);
    }

    [Test]
    public void Keys_use_correct_33_byte_format()
    {
        UInt256 blockId = 42;
        L1Origin origin = new(blockId, Hash256.Zero, 100, Hash256.Zero, null);

        _store.WriteL1Origin(blockId, origin);

        var testDb = (TestMemDb)_db;
        var allKeys = testDb.Keys.ToArray();
        allKeys.Should().HaveCount(1);
        allKeys[0].Length.Should().Be(33, "Keys should be 33 bytes (1 prefix + 32 UInt256)");
        allKeys[0][0].Should().Be(0x00, "L1Origin keys should have prefix 0x00");
    }

    [Test]
    public void Batch_keys_use_correct_prefix()
    {
        _store.WriteBatchToLastBlockID(1, 100);

        var testDb = (TestMemDb)_db;
        var allKeys = testDb.Keys.ToArray();
        allKeys.Should().HaveCount(1);
        allKeys[0].Length.Should().Be(33);
        allKeys[0][0].Should().Be(0x01, "Batch keys should have prefix 0x01");
    }

    [Test]
    public void Head_key_uses_correct_prefix()
    {
        _store.WriteHeadL1Origin(1);

        var testDb = (TestMemDb)_db;
        var allKeys = testDb.Keys.ToArray();
        allKeys.Should().HaveCount(1);
        allKeys[0].Length.Should().Be(33);
        allKeys[0][0].Should().Be(0xFF, "Head key should have prefix 0xFF");
    }

    [Test]
    public void Can_store_and_retrieve_signature()
    {
        UInt256 blockId = 123;
        int[] signature = Enumerable.Range(0, 65).ToArray();
        L1Origin origin = new(blockId, Hash256.Zero, 456, Hash256.Zero, null) { Signature = signature };

        _store.WriteL1Origin(blockId, origin);
        L1Origin? retrieved = _store.ReadL1Origin(blockId);

        retrieved.Should().NotBeNull();
        retrieved!.Signature.Should().NotBeNull();
        retrieved.Signature!.Length.Should().Be(65);
        retrieved.Signature.Should().BeEquivalentTo(signature);
    }

    [Test]
    public void Can_write_and_read_l1_origin_with_null_block_height()
    {
        UInt256 blockId = 456;
        L1Origin origin = new(blockId, Hash256.Zero, null, Hash256.Zero, null);

        _store.WriteL1Origin(blockId, origin);
        L1Origin? retrieved = _store.ReadL1Origin(blockId);

        retrieved.Should().NotBeNull();
        retrieved!.L1BlockHeight.Should().Be(0);
        retrieved.IsPreconfBlock.Should().BeTrue();
    }
}

