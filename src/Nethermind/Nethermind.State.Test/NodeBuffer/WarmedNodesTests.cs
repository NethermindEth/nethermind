// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.Store.Test.NodeBuffer;

internal class WarmedNodesTests
{
    private const int ChunkBytes = 1 << 20;
    private const int LargeRecordCount = 5000;

    [Test]
    public void Get_checks_hash_insert_verifies_and_reset_clears([Values] bool storage)
    {
        WarmedNodes warmed = new(1024);
        Hash256 address = storage ? TestItem.KeccakA : null;
        Hash256 otherAddress = storage ? null : TestItem.KeccakA;
        byte[] rlp = NodeRecordBufferTests.Leaf(0, 8);
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        byte[] otherRlp = NodeRecordBufferTests.Leaf(1, 8);
        ValueHash256 otherHash = ValueKeccak.Compute(otherRlp);
        byte[] truncatedExtensionRlp = Bytes.FromHexString("e215a0" + "22222222222222222222222222222222222222222222222222222222222222");
        TreePath path = Path(0);
        TreePath collidingPath = CollidingPath(address, path);

        bool inserted = warmed.TryInsert(address, path, hash, rlp, out NodeRecord record, out ulong pointer);
        bool reinserted = warmed.TryInsert(address, path, hash, rlp, out _, out ulong repeatedPointer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inserted && reinserted, Is.True, "insert");
            Assert.That(record.Rlp.ToArray(), Is.EqualTo(rlp), "inserted RLP");
            Assert.That(record.Hash, Is.EqualTo(hash), "inserted hash");
            Assert.That(NodeRecordCodec.IsTransient(pointer), Is.True, "temporary generation");
            Assert.That(repeatedPointer, Is.EqualTo(pointer), "duplicate reuses its pointer");
            Assert.That(warmed.TryGet(address, path, hash, out _, out ulong indexedPointer) && indexedPointer == pointer, Is.True, "indexed pointer");
            Assert.That(warmed.TryGet(pointer, otherHash, out _), Is.False, "pointer checks expected hash");
            Assert.That(new WarmedNodes(1).TryGet(pointer, hash, out _), Is.False, "foreign resource");
            Assert.That(warmed.Count, Is.EqualTo(1), "the first record of a position and hash is kept");
            Assert.That(Get(warmed, address, path, hash), Is.EqualTo(rlp), "get");
            Assert.That(Get(warmed, address, path, otherHash), Is.Null, "wrong hash");
            Assert.That(Get(warmed, otherAddress, path, hash), Is.Null, "other address");
            Assert.That(Get(warmed, address, Path(1), hash), Is.Null, "other path");
            Assert.That(warmed.TryInsert(address, Path(2), otherHash, rlp, out _), Is.False, "keccak mismatch");
            Assert.That(warmed.TryInsert(address, Path(3), ValueKeccak.Compute(truncatedExtensionRlp), truncatedExtensionRlp, out _), Is.False, "invalid RLP");
            Assert.That(Get(warmed, address, Path(2), otherHash) ?? Get(warmed, address, Path(3), ValueKeccak.Compute(truncatedExtensionRlp)), Is.Null, "rejected inserts");
        }

        warmed.TryInsert(address, collidingPath, otherHash, otherRlp, out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Get(warmed, address, path, hash), Is.Null, "replaced by a collision");
            Assert.That(Get(warmed, address, collidingPath, otherHash), Is.EqualTo(otherRlp), "collision");
            Assert.That(warmed.TryGet(pointer, hash, out NodeRecord displaced) && displaced.Rlp.SequenceEqual(rlp), Is.True, "pointer survives index displacement");
        }

        long initialArenaBytes = warmed.ArenaBytes;
        List<(NodeRecord Record, byte[] Rlp, ulong Pointer)> largeRecords = [];
        for (int i = 0; i < LargeRecordCount; i++)
        {
            byte[] largeRlp = NodeRecordBufferTests.Leaf(i, 400);
            warmed.TryInsert(address, Path(1000 + i), ValueKeccak.Compute(largeRlp), largeRlp, out NodeRecord largeRecord, out ulong largePointer);
            largeRecords.Add((largeRecord, largeRlp, largePointer));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warmed.Count, Is.EqualTo(2 + LargeRecordCount), "includes displaced records");
            Assert.That(initialArenaBytes, Is.EqualTo(ChunkBytes), "one chunk");
            Assert.That(warmed.ArenaBytes, Is.EqualTo(3 * ChunkBytes), "grows across chunk boundaries");
            Assert.That(largeRecords.Select(large => large.Record.Rlp.SequenceEqual(large.Rlp)), Is.All.True, "records in every chunk");
            Assert.That(largeRecords.Select(large => warmed.TryGet(large.Pointer, large.Record.Hash, out NodeRecord found) && found.Rlp.SequenceEqual(large.Rlp)), Is.All.True, "pointers into every chunk");
        }

        warmed.Reset();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warmed.Count, Is.Zero, "count after reset");
            Assert.That(warmed.ArenaBytes, Is.EqualTo(ChunkBytes), "the first chunk is kept");
            Assert.That(Get(warmed, address, collidingPath, otherHash), Is.Null, "nothing remains");
            Assert.That(Enumerable.Range(0, LargeRecordCount).Select(i => Get(warmed, address, Path(1000 + i), largeRecords[i].Record.Hash)), Is.All.Null, "nothing remains");
        }

        warmed.TryInsert(address, path, hash, rlp, out _, out ulong resetPointer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Get(warmed, address, path, hash), Is.EqualTo(rlp), "reusable after reset");
            Assert.That(resetPointer, Is.Not.EqualTo(pointer), "reset renews identity even at the same arena location");
            Assert.That(warmed.TryGet(pointer, hash, out _), Is.False, "pre-reset pointer cannot alias the same record");
            Assert.That(warmed.TryGet(resetPointer, hash, out _), Is.True, "new pointer resolves");
        }
    }

    [Test]
    public void Index_checks_the_full_address_even_when_node_keys_are_equal()
    {
        WarmedNodes warmed = new(1);
        byte[] addressBytes = TestItem.KeccakA.Bytes.ToArray();
        addressBytes[^1] ^= 1;
        Hash256 otherAddress = new(addressBytes);
        TreePath path = Path(0);
        byte[] rlp = NodeRecordBufferTests.Leaf(0, 8);
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        Assert.That(NodeRecordCodec.NodeKey(TestItem.KeccakA, path), Is.EqualTo(NodeRecordCodec.NodeKey(otherAddress, path)), "same mixed key");

        warmed.TryInsert(TestItem.KeccakA, path, hash, rlp, out _, out ulong originalPointer);
        Assert.That(Get(warmed, otherAddress, path, hash), Is.Null, "a matching node key is not a matching address");
        warmed.TryInsert(otherAddress, path, hash, rlp, out _, out ulong otherPointer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warmed.Count, Is.EqualTo(2), "different addresses must not deduplicate");
            Assert.That(Get(warmed, TestItem.KeccakA, path, hash), Is.Null, "original address displaced");
            Assert.That(Get(warmed, otherAddress, path, hash), Is.EqualTo(rlp));
            Assert.That(otherPointer, Is.Not.EqualTo(originalPointer));
            Assert.That(warmed.TryGet(originalPointer, hash, out _), Is.True, "the displaced record remains addressable");
        }
    }

    [Test]
    public void Concurrent_insertion_publishes_complete_records([Values] bool samePosition)
    {
        const int count = 1000;
        WarmedNodes warmed = new(1);
        byte[] rlp = NodeRecordBufferTests.Leaf(0, 400);
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        bool[] complete = new bool[count];
        ulong[] pointers = new ulong[count];

        Parallel.For(0, count, i =>
        {
            TreePath path = Path(samePosition ? 0 : i);
            bool inserted = warmed.TryInsert(null, path, hash, rlp, out NodeRecord record, out pointers[i]);
            bool resolved = warmed.TryGet(pointers[i], hash, out NodeRecord located);
            bool indexed = !warmed.TryGet(null, path, hash, out NodeRecord current) || current.Rlp.SequenceEqual(rlp);
            complete[i] = inserted && resolved && record.Rlp.SequenceEqual(rlp) && located.Rlp.SequenceEqual(rlp) && indexed;
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(complete, Is.All.True);
            Assert.That(warmed.Count, Is.EqualTo(samePosition ? 1 : count));
            Assert.That(pointers.Distinct().Count(), Is.EqualTo(samePosition ? 1 : count));
        }
    }

    [Test]
    public void Transient_generation_exhaustion_does_not_reuse_ids()
    {
        long counter = NodeRecordCodec.TransientGenerationMask - 2L;
        uint last = NodeRecordCodec.NextTransientGeneration(ref counter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(last, Is.EqualTo(uint.MaxValue));
            Assert.That(() => NodeRecordCodec.NextTransientGeneration(ref counter), Throws.TypeOf<TrieException>());
            Assert.That(() => NodeRecordCodec.NextTransientGeneration(ref counter), Throws.TypeOf<TrieException>(), "exhaustion remains terminal");
        }
    }

    [Test]
    public void A_record_larger_than_a_chunk_is_not_published()
    {
        WarmedNodes warmed = new(1);
        byte[] rlp = NodeRecordBufferTests.Leaf(0, ChunkBytes);
        bool added = warmed.TryInsert(null, TreePath.Empty, ValueKeccak.Compute(rlp), rlp, out NodeRecord record, out ulong pointer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(added, Is.False);
            Assert.That(record.IsNull, Is.True);
            Assert.That(pointer, Is.Zero);
            Assert.That(warmed.Count, Is.Zero);
            Assert.That(warmed.ArenaBytes, Is.Zero);
        }
    }

    private static TreePath Path(int seed) => new(ValueKeccak.Compute(BitConverter.GetBytes(seed)), 64);

    /// <summary>A path whose node key shares the low 16 bits with <paramref name="path"/>'s, so it takes the same slot in any table of up to 65536 slots.</summary>
    private static TreePath CollidingPath(Hash256 address, in TreePath path)
    {
        ulong nodeKey = NodeRecordCodec.NodeKey(address, path);
        for (int seed = 1_000_000; ; seed++)
        {
            TreePath candidate = Path(seed);
            ulong candidateKey = NodeRecordCodec.NodeKey(address, candidate);
            if (((candidateKey ^ nodeKey) & 0xFFFF) == 0 && candidateKey != nodeKey) return candidate;
        }
    }

    private static byte[] Get(WarmedNodes warmed, Hash256 address, in TreePath path, in ValueHash256 hash) =>
        warmed.TryGet(address, path, hash, out NodeRecord record) ? record.Rlp.ToArray() : null;
}
