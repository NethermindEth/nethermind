// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;
using NUnit.Framework;

namespace Nethermind.Store.Test.NodeLog;

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
        byte[] rlp = TrieNodeLogTests.Leaf(0, 8);
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        byte[] otherRlp = TrieNodeLogTests.Leaf(1, 8);
        ValueHash256 otherHash = ValueKeccak.Compute(otherRlp);
        byte[] truncatedExtensionRlp = Bytes.FromHexString("e215a0" + "22222222222222222222222222222222222222222222222222222222222222");
        TreePath path = Path(0);
        TreePath collidingPath = CollidingPath(address, path);

        bool inserted = warmed.TryInsert(address, path, hash, rlp, out NodeRecordRef record);
        bool reinserted = warmed.TryInsert(address, path, hash, rlp, out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inserted && reinserted, Is.True, "insert");
            Assert.That(record.Rlp.ToArray(), Is.EqualTo(rlp), "inserted RLP");
            Assert.That(record.Hash, Is.EqualTo(hash), "inserted hash");
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
        }

        long initialArenaBytes = warmed.ArenaBytes;
        List<(NodeRecordRef Record, byte[] Rlp)> largeRecords = [];
        for (int i = 0; i < LargeRecordCount; i++)
        {
            byte[] largeRlp = TrieNodeLogTests.Leaf(i, 400);
            warmed.TryInsert(address, Path(1000 + i), ValueKeccak.Compute(largeRlp), largeRlp, out NodeRecordRef largeRecord);
            largeRecords.Add((largeRecord, largeRlp));
        }

        List<(Hash256 Address, TreePath Path, NodeRecordRef Record)> copied = [];
        warmed.CopyAllTo(copied);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(copied.Select(copy => copy.Record.Rlp.ToArray()), Is.EqualTo(new[] { rlp, otherRlp }.Concat(largeRecords.Select(large => large.Rlp))), "every record in arena order");
            Assert.That(copied.Select(copy => (copy.Address, copy.Path)),
                Is.EqualTo(new[] { path, collidingPath }.Concat(Enumerable.Range(0, LargeRecordCount).Select(i => Path(1000 + i))).Select(insertedPath => (address, insertedPath))),
                "with the address and path each was inserted for");
            Assert.That(initialArenaBytes, Is.EqualTo(ChunkBytes), "one chunk");
            Assert.That(warmed.ArenaBytes, Is.EqualTo(3 * ChunkBytes), "grows across chunk boundaries");
            Assert.That(largeRecords.Select(large => large.Record.Rlp.SequenceEqual(large.Rlp)), Is.All.True, "records in every chunk");
        }

        warmed.Reset();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warmed.Count, Is.Zero, "count after reset");
            Assert.That(warmed.ArenaBytes, Is.EqualTo(ChunkBytes), "the first chunk is kept");
            Assert.That(Get(warmed, address, collidingPath, otherHash), Is.Null, "nothing remains");
            Assert.That(Enumerable.Range(0, LargeRecordCount).Select(i => Get(warmed, address, Path(1000 + i), largeRecords[i].Record.Hash)), Is.All.Null, "nothing remains");
        }

        warmed.TryInsert(address, path, hash, rlp, out _);
        Assert.That(Get(warmed, address, path, hash), Is.EqualTo(rlp), "reusable after reset");
    }

    [Test]
    public void Record_table_add_fails_once_the_arena_budget_is_spent()
    {
        const int lastChunkBytes = 1000;
        RecordTable table = new(1024, ChunkBytes + lastChunkBytes + NodeRecord.RecordAlignment - 1);
        int alignedRecordLength = NodeRecord.AlignedLength(Record(Path(0), TrieNodeLogTests.Leaf(0, 400)).Length);
        int fittingRecordCount = ChunkBytes / alignedRecordLength + lastChunkBytes / alignedRecordLength;

        bool allAdded = Enumerable.Range(0, fittingRecordCount).All(i => table.TryAdd(null, Path(i), Record(Path(i), TrieNodeLogTests.Leaf(i, 400)), out _));
        bool overBudgetAdded = table.TryAdd(null, Path(fittingRecordCount), Record(Path(fittingRecordCount), TrieNodeLogTests.Leaf(fittingRecordCount, 400)), out NodeRecordRef overBudget);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(allAdded, Is.True, "adds within the budget");
            Assert.That(overBudgetAdded, Is.False, "add over the budget");
            Assert.That(overBudget.IsNull, Is.True, "no record over the budget");
            Assert.That(table.Count, Is.EqualTo(fittingRecordCount), "count");
            Assert.That(table.ArenaBytes, Is.EqualTo(ChunkBytes + lastChunkBytes), "the last chunk is shortened to the budget");
        }
    }

    private static byte[] Record(in TreePath path, byte[] rlp)
    {
        NodeRecord.TryMeasure(rlp, out NodeRecordKind kind, out ushort hashMask);
        byte[] record = new byte[NodeRecord.Length(BitOperations.PopCount(hashMask), rlp.Length)];
        NodeRecord.WriteFromRlp(rlp, kind, hashMask, ValueKeccak.Compute(rlp), NodeRecord.NodeKey(null, path), record);
        return record;
    }

    private static TreePath Path(int seed) => new(ValueKeccak.Compute(BitConverter.GetBytes(seed)), 64);

    /// <summary>A path whose node key shares the low 16 bits with <paramref name="path"/>'s, so it takes the same slot in any table of up to 65536 slots.</summary>
    private static TreePath CollidingPath(Hash256 address, in TreePath path)
    {
        ulong nodeKey = NodeRecord.NodeKey(address, path);
        for (int seed = 1_000_000; ; seed++)
        {
            TreePath candidate = Path(seed);
            ulong candidateKey = NodeRecord.NodeKey(address, candidate);
            if (((candidateKey ^ nodeKey) & 0xFFFF) == 0 && candidateKey != nodeKey) return candidate;
        }
    }

    private static byte[] Get(WarmedNodes warmed, Hash256 address, in TreePath path, in ValueHash256 hash) =>
        warmed.TryGet(address, path, hash, out NodeRecordRef record) ? record.Rlp.ToArray() : null;
}
