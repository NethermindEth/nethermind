// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test.NodeBuffer;

public class PbtWarmedGroupLogTests
{
    [Test]
    public void Concurrent_appends_publish_complete_records([Values] bool samePosition)
    {
        const int count = 1000;
        using PbtWarmedGroupLog warmed = new(1);
        byte[] payload = PbtGroupRecordBufferTests.Payload(0, 400);
        bool[] complete = new bool[count];

        Parallel.For(0, count, i =>
        {
            PbtStorageNodePath path = Path(samePosition ? 0 : i);
            warmed.Append(path, payload, out PbtGroupRecord record);
            bool indexed = warmed.TryGet(path, out PbtGroupRecord current) && current.Payload.Span.SequenceEqual(payload);
            complete[i] = record.Payload.Span.SequenceEqual(payload) && record.Path.Equals(path) && indexed;
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(complete, Is.All.True);
            Assert.That(warmed.Count, Is.EqualTo(samePosition ? 1 : count));
        }
    }

    public static PbtStorageNodePath Path(int seed) => PbtTestPaths.Prefix<PbtStorageNodePath>(ValueKeccak.Compute(BitConverter.GetBytes(seed)).Bytes, 255);
}
