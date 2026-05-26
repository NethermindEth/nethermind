// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using FluentAssertions;
using Nethermind.Blockchain.Headers;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.BlockAccessLists;

[Parallelizable(ParallelScope.All)]
public class BlockAccessListStoreTests
{
    [Test]
    public void Round_trips_byte_array_payload()
    {
        TestMemDb db = new();
        BlockAccessListStore store = new(db);

        const long blockNumber = 42;
        Hash256 blockHash = TestItem.KeccakA;
        byte[] encoded = [0xc1, 0x80];

        store.Insert(blockNumber, blockHash, encoded);

        store.Exists(blockNumber, blockHash).Should().BeTrue();
        using MemoryManager<byte>? rlp = store.GetRlp(blockNumber, blockHash);
        rlp!.Memory.ToArray().Should().Equal(encoded);
    }

    [Test]
    public void Round_trips_decoded_block_access_list()
    {
        TestMemDb db = new();
        BlockAccessListStore store = new(db);

        const long blockNumber = 1_000_000;
        Hash256 blockHash = TestItem.KeccakB;
        ReadOnlyBlockAccessList bal = new();

        store.Insert(blockNumber, blockHash, bal);

        ReadOnlyBlockAccessList? retrieved = store.Get(blockNumber, blockHash);
        retrieved.Should().NotBeNull();

        using MemoryManager<byte>? rlp = store.GetRlp(blockNumber, blockHash);
        rlp!.Memory.ToArray().Should().Equal(Rlp.Encode(bal).Bytes);
    }

    [Test]
    public void Delete_removes_entry()
    {
        TestMemDb db = new();
        BlockAccessListStore store = new(db);

        const long blockNumber = 5;
        Hash256 blockHash = TestItem.KeccakC;
        byte[] encoded = [0xc1, 0x80];
        store.Insert(blockNumber, blockHash, encoded);

        store.Delete(blockNumber, blockHash);

        store.Exists(blockNumber, blockHash).Should().BeFalse();
        store.GetRlp(blockNumber, blockHash).Should().BeNull();
        store.Get(blockNumber, blockHash).Should().BeNull();
    }

    [Test]
    public void Same_hash_at_different_numbers_does_not_collide()
    {
        TestMemDb db = new();
        BlockAccessListStore store = new(db);

        Hash256 blockHash = TestItem.KeccakA;
        byte[] balLow = [0xc1, 0x01];
        byte[] balHigh = [0xc1, 0x02];

        store.Insert(1, blockHash, balLow);
        store.Insert(2, blockHash, balHigh);

        using MemoryManager<byte>? rlpLow = store.GetRlp(1, blockHash);
        using MemoryManager<byte>? rlpHigh = store.GetRlp(2, blockHash);

        rlpLow!.Memory.ToArray().Should().Equal(balLow);
        rlpHigh!.Memory.ToArray().Should().Equal(balHigh);

        store.Delete(1, blockHash);
        store.Exists(1, blockHash).Should().BeFalse();
        store.Exists(2, blockHash).Should().BeTrue();
    }

    [Test]
    public void Stores_under_block_number_prefixed_key()
    {
        TestMemDb db = new();
        BlockAccessListStore store = new(db);

        const long blockNumber = 7;
        Hash256 blockHash = TestItem.KeccakA;
        byte[] encoded = [0xc1, 0x80];

        store.Insert(blockNumber, blockHash, encoded);

        byte[] expectedKey = Bytes.Concat(blockNumber.ToBigEndianByteArray(), blockHash.BytesToArray());
        db[expectedKey].Should().Equal(encoded);
        db[blockHash.Bytes].Should().BeNull();
    }
}
