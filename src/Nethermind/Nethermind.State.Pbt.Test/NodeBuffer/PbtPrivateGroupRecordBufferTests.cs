// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test.NodeBuffer;

public class PbtPrivateGroupRecordBufferTests
{
    private const int GenerationBytes = 4096;

    [Test]
    public void Keeps_every_record_and_frees_storage_after_the_last_pin()
    {
        PbtPrivateGroupRecordBuffer buffer = new(GenerationBytes);
        IPbtGroupRecordBufferPin pin = buffer.PinHead();
        List<(ulong Pointer, byte[] Payload)> records = [.. Enumerable.Range(0, 100).Select(i => PbtGroupRecordBufferTests.Payload(i, 1000)).Select(payload => (PbtGroupRecordBufferTests.AppendRoot(buffer, payload), payload))];
        byte[] oversize = PbtGroupRecordBufferTests.Payload(-1, 2 * GenerationBytes);
        records.Add((PbtGroupRecordBufferTests.AppendRoot(buffer, oversize), oversize));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(records.Select(record => record.Pointer >> 32).Distinct().Count(), Is.GreaterThan(20), "records span many generations");
            Assert.That(records.Select(record => buffer.TryLocate(record.Pointer, out PbtGroupRecord located) && located.Payload.Span.SequenceEqual(record.Payload)), Is.All.True, "no generation is dropped");
            Assert.That(records.Select(record => buffer.FindRoot(ValueKeccak.Compute(record.Payload))), Is.EqualTo(records.Select(record => record.Pointer)), "roots");
            Assert.That(records[0].Pointer, Is.GreaterThanOrEqualTo(pin.WriteBoundary), "writes follow the pin boundary");
        }

        pin.Dispose();
        IPbtGroupRecordBufferPin reader = buffer.PinHead();
        IPbtGroupRecordBufferPin readerCopy = reader.Copy();
        Assert.That(buffer.TryLocate(records[0].Pointer, out PbtGroupRecord record), Is.True);
        buffer.Dispose();
        reader.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.Payload.ToArray(), Is.EqualTo(records[0].Payload), "a pin keeps storage past dispose");
            Assert.That(() => buffer.PinHead(), Throws.InstanceOf<ObjectDisposedException>());
        }

        readerCopy.Dispose();
        Assert.That(() => record.Payload.ToArray(), Throws.InstanceOf<ObjectDisposedException>(), "the last pin frees storage");
    }

    [Test]
    public void Clear_frees_every_record_and_root_and_later_pointers_move_past_them()
    {
        using PbtPrivateGroupRecordBuffer buffer = new(GenerationBytes);
        List<(ulong Pointer, byte[] Payload)> records = [.. Enumerable.Range(0, 10).Select(i => PbtGroupRecordBufferTests.Payload(i, 1000)).Select(payload => (PbtGroupRecordBufferTests.AppendRoot(buffer, payload), payload))];

        buffer.Clear();

        byte[] payload = records[0].Payload;
        ulong appended = PbtGroupRecordBufferTests.AppendRoot(buffer, payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(records.Select(record => buffer.TryLocate(record.Pointer, out _)), Is.All.False, "records before the clear");
            Assert.That(records.Skip(1).Select(record => buffer.FindRoot(ValueKeccak.Compute(record.Payload))), Is.All.Zero, "roots before the clear");
            Assert.That(appended, Is.GreaterThan(records.Max(record => record.Pointer)), "a clear never reuses a pointer");
            Assert.That(buffer.TryLocate(appended, out PbtGroupRecord located) && located.Payload.Span.SequenceEqual(payload), Is.True, "records after the clear");
        }
    }
}
