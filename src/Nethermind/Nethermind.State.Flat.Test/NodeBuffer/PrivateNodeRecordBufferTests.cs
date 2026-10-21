// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.NodeBuffer;

internal class PrivateNodeRecordBufferTests
{
    private const int GenerationBytes = 4096;

    [Test]
    public void Keeps_every_record_until_cleared_and_frees_storage_after_the_last_pin()
    {
        PrivateNodeRecordBuffer buffer = new(GenerationBytes);
        buffer.Clear();
        INodeRecordBufferPin pin = buffer.PinHead();
        List<(ulong Pointer, byte[] Rlp)> records = [.. Enumerable.Range(0, 100).Select(i => NodeRecordBufferTests.Leaf(i, 1000)).Select(rlp => (Append(buffer, rlp), rlp))];
        byte[] oversize = NodeRecordBufferTests.Leaf(-1, 2 * GenerationBytes);
        records.Add((Append(buffer, oversize), oversize));
        ulong lastPointer = records[^1].Pointer;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(records.Select(record => record.Pointer >> 32).Distinct().Count(), Is.GreaterThan(20), "records span many generations");
            Assert.That(records.Select(record => buffer.TryLocate(record.Pointer, out NodeRecord located) && located.Rlp.SequenceEqual(record.Rlp)), Is.All.True, "no generation is dropped");
            Assert.That(records.Select(record => buffer.FindRoot(null, ValueKeccak.Compute(record.Rlp), ulong.MaxValue)), Is.EqualTo(records.Select(record => record.Pointer)), "roots");
            Assert.That(records[0].Pointer, Is.GreaterThanOrEqualTo(pin.WriteBoundary), "writes follow the pin boundary");
        }

        buffer.TryLocate(records[0].Pointer, out NodeRecord firstBeforeClear);
        pin.Dispose();
        buffer.Clear();
        ulong afterClear = Append(buffer, records[0].Rlp);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(records.Select(record => buffer.TryLocate(record.Pointer, out _)), Is.All.False, "cleared records miss");
            Assert.That(buffer.FindRoot(null, ValueKeccak.Compute(records[1].Rlp), ulong.MaxValue), Is.Zero, "cleared roots miss");
            Assert.That(afterClear, Is.GreaterThan(lastPointer), "pointers continue past the cleared records");
            Assert.That(firstBeforeClear.Rlp.ToArray(), Is.EqualTo(records[0].Rlp), "the first generation's storage is reused");
        }

        INodeRecordBufferPin reader = buffer.PinHead();
        INodeRecordBufferPin readerCopy = reader.Copy();
        Assert.That(buffer.TryLocate(afterClear, out NodeRecord record), Is.True);
        buffer.Dispose();
        reader.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.Rlp.ToArray(), Is.EqualTo(records[0].Rlp), "a pin keeps storage past dispose");
            Assert.That(() => buffer.PinHead(), Throws.InstanceOf<ObjectDisposedException>());
        }

        readerCopy.Dispose();
        Assert.That(() => record.Rlp.ToArray(), Throws.InstanceOf<ObjectDisposedException>(), "the last pin frees storage");
    }

    private static ulong Append(PrivateNodeRecordBuffer buffer, byte[] rlp)
    {
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        ulong pointer = buffer.AppendRecordFromRlp(null, TreePath.Empty, hash, rlp, []);
        buffer.SetRoot(null, hash, pointer);
        return pointer;
    }
}
