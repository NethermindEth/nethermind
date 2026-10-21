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
    public void Reset_ClearsTheWarmedGroupLogAndGrowsItsSizeOnlyPastItsCapacity([Values] bool pastCapacity)
    {
        const int capacity = 4;
        using PbtTransientResource resource = new(new PbtTransientResource.Size(capacity));
        PbtWarmedGroupLog warmedGroupLog = resource.WarmedGroupLog;
        int groupCount = pastCapacity ? capacity + 1 : capacity;
        for (byte value = 0; value < groupCount; value++)
            warmedGroupLog.Append(PbtWarmedGroupLogTests.Path(value), PbtGroupRecordBufferTests.Payload(value, 64), out _);

        resource.Reset();

        bool foundAfterReset = warmedGroupLog.TryGet(PbtWarmedGroupLogTests.Path(0), out _);
        byte[] payload = PbtGroupRecordBufferTests.Payload(0, 64);
        warmedGroupLog.Append(PbtWarmedGroupLogTests.Path(0), payload, out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resource.WarmedGroupLog, Is.SameAs(warmedGroupLog));
            Assert.That(foundAfterReset, Is.False);
            Assert.That(resource.GetSize().NodeGroupsCacheSize, Is.EqualTo(groupCount));
            Assert.That(warmedGroupLog.TryGet(PbtWarmedGroupLogTests.Path(0), out PbtGroupRecord record) && record.Payload.Span.SequenceEqual(payload), Is.True);
        }
    }
}
