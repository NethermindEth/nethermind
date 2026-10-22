// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.NodeBuffer;

internal class WarmedNodeBufferTests
{
    private const int GenerationBytes = 1 << 20;
    private const int LargeRecordCount = 5000;

    [Test]
    public void Written_records_are_indexed_by_position_and_resolve_through_the_combined_buffer([Values] bool storage)
    {
        using WarmedNodeBuffer warmed = new(1024);
        using NodeRecordBuffer global = new(0, GenerationBytes, NodeRecordBuffer.DefaultLiveGenerations);
        WarmedNodeRecordBuffer combined = new(global, warmed);
        Hash256? address = storage ? TestItem.KeccakA : null;
        Hash256? otherAddress = storage ? null : TestItem.KeccakA;
        byte[] addressBytes = TestItem.KeccakA.Bytes.ToArray();
        addressBytes[^1] ^= 1;
        Hash256 sameNodeKeyHashAddress = new(addressBytes);
        byte[] rlp = NodeRecordBufferTests.Leaf(0, 8);
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        byte[] otherRlp = NodeRecordBufferTests.Leaf(1, 8);
        byte[] oversizedRlp = NodeRecordBufferTests.Leaf(2, 2 * GenerationBytes);
        TreePath path = Path(0);

        ulong pointer = FlatTestHelpers.Warm(warmed, address, path, rlp);
        warmed.TryLocate(pointer, out NodeRecord record);
        ulong repeatedPointer = FlatTestHelpers.Warm(warmed, address, path, rlp);
        ulong globalPointer = global.AppendRecordFromRlp(address, path, hash, rlp, []);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.Rlp.ToArray(), Is.EqualTo(rlp), "appended RLP");
            Assert.That(record.Hash, Is.EqualTo(hash), "appended hash");
            Assert.That(repeatedPointer, Is.Not.EqualTo(pointer), "a repeated write writes a new record");
            Assert.That(warmed.Count, Is.EqualTo(1), "one position");
            Assert.That(warmed.TryGet(address, path, out _, out ulong indexedPointer) && indexedPointer == repeatedPointer, Is.True, "the latest record is indexed");
            Assert.That(Get(warmed, otherAddress, path), Is.Null, "other address");
            Assert.That(Get(warmed, sameNodeKeyHashAddress, path), Is.Null, "a matching node key hash is not a matching address");
            Assert.That(Get(warmed, address, Path(1)), Is.Null, "other path");
            warmed.TryLocate(FlatTestHelpers.Warm(warmed, address, Path(4), oversizedRlp), out NodeRecord oversized);
            Assert.That(oversized.Rlp.ToArray(), Is.EqualTo(oversizedRlp), "a record larger than a generation");
            Assert.That(combined.TryLocate(pointer, out NodeRecord located) && located.Rlp.SequenceEqual(rlp), Is.True, "combined resolves the transient pointer");
            Assert.That(combined.TryLocate(globalPointer, out located) && located.Rlp.SequenceEqual(rlp), Is.True, "combined resolves the global pointer");
            Assert.That(combined.FindRoot(address, hash, ulong.MaxValue), Is.Zero, "roots stay global");
            Assert.That(global.TryLocate(pointer, out _), Is.False, "the global buffer misses transient pointers");
            Assert.That(warmed.TryLocate(globalPointer, out _), Is.False, "the warmed buffer misses global pointers");
        }

        ulong repeatedPositionPointer = FlatTestHelpers.Warm(warmed, address, path, otherRlp);
        List<(byte[] Rlp, ulong Pointer)> largeRecords = [];
        for (int i = 0; i < LargeRecordCount; i++)
        {
            byte[] largeRlp = NodeRecordBufferTests.Leaf(i, 400);
            ulong largePointer = FlatTestHelpers.Warm(warmed, address, Path(1000 + i), largeRlp);
            largeRecords.Add((largeRlp, largePointer));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Get(warmed, address, path), Is.EqualTo(otherRlp), "a later node replaces the position");
            Assert.That(repeatedPositionPointer, Is.Not.EqualTo(repeatedPointer), "a new record");
            Assert.That(combined.TryLocate(pointer, out NodeRecord displaced) && displaced.Rlp.SequenceEqual(rlp), Is.True, "a replaced record stays readable by pointer");
            Assert.That(largeRecords.Select(large => combined.TryLocate(large.Pointer, out NodeRecord found) && found.Rlp.SequenceEqual(large.Rlp)), Is.All.True, "pointers into every generation");
        }

        warmed.Reset();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warmed.Count, Is.Zero, "count after reset");
            Assert.That(Get(warmed, address, path), Is.Null, "nothing remains");
            Assert.That(Enumerable.Range(0, LargeRecordCount).Select(i => Get(warmed, address, Path(1000 + i))), Is.All.Null, "nothing remains");
        }

        ulong resetPointer = FlatTestHelpers.Warm(warmed, address, path, rlp);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Get(warmed, address, path), Is.EqualTo(rlp), "reusable after reset");
            Assert.That(resetPointer, Is.Not.EqualTo(pointer), "writes continue past the records before the reset");
        }
    }

    [Test]
    public void Concurrent_writes_publish_complete_records([Values] bool samePosition)
    {
        const int count = 1000;
        using WarmedNodeBuffer warmed = new(1);
        byte[] rlp = NodeRecordBufferTests.Leaf(0, 400);
        bool[] complete = new bool[count];

        Parallel.For(0, count, i =>
        {
            TreePath path = Path(samePosition ? 0 : i);
            ulong pointer = FlatTestHelpers.Warm(warmed, null, path, rlp);
            bool resolved = warmed.TryLocate(pointer, out NodeRecord located);
            bool indexed = warmed.TryGet(null, path, out NodeRecord current, out _) && current.Rlp.SequenceEqual(rlp);
            complete[i] = resolved && located.Rlp.SequenceEqual(rlp) && indexed;
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(complete, Is.All.True);
            Assert.That(warmed.Count, Is.EqualTo(samePosition ? 1 : count));
        }
    }

    private static TreePath Path(int seed) => new(ValueKeccak.Compute(BitConverter.GetBytes(seed)), 64);

    private static byte[]? Get(WarmedNodeBuffer warmed, Hash256? address, in TreePath path) =>
        warmed.TryGet(address, path, out NodeRecord record, out _) ? record.Rlp.ToArray() : null;
}
