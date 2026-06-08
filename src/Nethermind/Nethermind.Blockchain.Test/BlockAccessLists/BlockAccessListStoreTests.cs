// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Blockchain.BlockAccessLists;
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

        const ulong blockNumber = 42;
        Hash256 blockHash = TestItem.KeccakA;
        byte[] encoded = [0xc1, 0x80];

        store.Insert(blockNumber, blockHash, encoded);

        Assert.That(store.Exists(blockNumber, blockHash), Is.True);
        using MemoryManager<byte>? rlp = store.GetRlp(blockNumber, blockHash);
        Assert.That(rlp!.Memory.ToArray(), Is.EqualTo(encoded));
    }

    [Test]
    public void Round_trips_decoded_block_access_list()
    {
        TestMemDb db = new();
        BlockAccessListStore store = new(db);

        const ulong blockNumber = 1_000_000;
        Hash256 blockHash = TestItem.KeccakB;
        ReadOnlyBlockAccessList bal = new();

        store.Insert(blockNumber, blockHash, bal);

        ReadOnlyBlockAccessList? retrieved = store.Get(blockNumber, blockHash);
        Assert.That(retrieved, Is.Not.Null);

        using MemoryManager<byte>? rlp = store.GetRlp(blockNumber, blockHash);
        Assert.That(rlp!.Memory.ToArray(), Is.EqualTo(Rlp.Encode(bal).Bytes));
    }

    [Test]
    public void Delete_removes_entry()
    {
        TestMemDb db = new();
        BlockAccessListStore store = new(db);

        const ulong blockNumber = 5;
        Hash256 blockHash = TestItem.KeccakC;
        byte[] encoded = [0xc1, 0x80];
        store.Insert(blockNumber, blockHash, encoded);

        store.Delete(blockNumber, blockHash);

        Assert.That(store.Exists(blockNumber, blockHash), Is.False);
        Assert.That(store.GetRlp(blockNumber, blockHash), Is.Null);
        Assert.That(store.Get(blockNumber, blockHash), Is.Null);
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

        Assert.That(rlpLow!.Memory.ToArray(), Is.EqualTo(balLow));
        Assert.That(rlpHigh!.Memory.ToArray(), Is.EqualTo(balHigh));

        store.Delete(1, blockHash);
        Assert.That(store.Exists(1, blockHash), Is.False);
        Assert.That(store.Exists(2, blockHash), Is.True);
    }

    [Test]
    public void Stores_under_block_number_prefixed_key()
    {
        TestMemDb db = new();
        BlockAccessListStore store = new(db);

        const ulong blockNumber = 7;
        Hash256 blockHash = TestItem.KeccakA;
        byte[] encoded = [0xc1, 0x80];

        store.Insert(blockNumber, blockHash, encoded);

        byte[] expectedKey = Bytes.Concat(blockNumber.ToBigEndianByteArray(), blockHash.BytesToArray());
        Assert.That(db[expectedKey], Is.EqualTo(encoded));
        Assert.That(db[blockHash.Bytes], Is.Null);
    }
}
