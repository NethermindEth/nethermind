// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Test.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtTransientResourceTests
{
    [Test]
    public void Reset_ClearsTheWarmedGroupBufferAndGrowsItsSizeOnlyPastItsCapacity([Values] bool pastCapacity)
    {
        const int capacity = 4;
        using PbtTransientResource resource = new(new PbtTransientResource.Size(capacity));
        PbtWarmedGroupBuffer warmedGroupBuffer = resource.WarmedGroupBuffer;
        int groupCount = pastCapacity ? capacity + 1 : capacity;
        ulong originalPointer = 0;
        for (byte value = 0; value < groupCount; value++)
        {
            ulong pointer = PbtSnapshotTestExtensions.Warm(warmedGroupBuffer, PbtWarmedGroupBufferTests.Path(value), PbtGroupRecordBufferTests.Payload(value, 64));
            if (value == 0) originalPointer = pointer;
        }

        resource.Reset();

        bool foundAfterReset = warmedGroupBuffer.TryGet(PbtWarmedGroupBufferTests.Path(0), out _, out _);
        byte[] payload = PbtGroupRecordBufferTests.Payload(0, 64);
        ulong newPointer = PbtSnapshotTestExtensions.Warm(warmedGroupBuffer, PbtWarmedGroupBufferTests.Path(0), payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resource.WarmedGroupBuffer, Is.SameAs(warmedGroupBuffer));
            Assert.That(foundAfterReset, Is.False);
            Assert.That(resource.GetSize().NodeGroupsCacheSize, Is.EqualTo(groupCount));
            Assert.That(newPointer, Is.Not.EqualTo(originalPointer), "a reset never reuses a pointer");
            Assert.That(warmedGroupBuffer.TryGet(PbtWarmedGroupBufferTests.Path(0), out PbtGroupRecord record, out _) && record.Payload.Span.SequenceEqual(payload), Is.True);
        }
    }
}
