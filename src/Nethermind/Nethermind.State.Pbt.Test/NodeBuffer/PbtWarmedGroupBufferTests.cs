// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test.NodeBuffer;

public class PbtWarmedGroupBufferTests
{
    private const int GenerationBytes = 1 << 20;
    private const int LargeRecordCount = 5000;

    [Test]
    public void Written_records_are_indexed_by_path()
    {
        using PbtWarmedGroupBuffer warmed = new(1024);
        byte[] payload = PbtGroupRecordBufferTests.Payload(0, 8);
        byte[] otherPayload = PbtGroupRecordBufferTests.Payload(1, 8);
        byte[] oversizedPayload = PbtGroupRecordBufferTests.Payload(2, 2 * GenerationBytes);
        PbtStorageNodePath path = Path(0);

        ulong pointer = PbtSnapshotTestExtensions.Warm(warmed, path, payload);
        ulong repeatedPointer = PbtSnapshotTestExtensions.Warm(warmed, path, payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warmed.TryGet(path, out PbtGroupRecord record) && record.Path.Equals(path), Is.True, "indexed at its path");
            Assert.That(Get(warmed, path), Is.EqualTo(payload), "written payload");
            Assert.That(repeatedPointer, Is.Not.EqualTo(pointer), "a repeated write writes a new record");
            Assert.That(warmed.Count, Is.EqualTo(1), "one path");
            Assert.That(Get(warmed, Path(1)), Is.Null, "other path");
            PbtSnapshotTestExtensions.Warm(warmed, Path(4), oversizedPayload);
            Assert.That(Get(warmed, Path(4)), Is.EqualTo(oversizedPayload), "a record larger than a generation");
        }

        PbtSnapshotTestExtensions.Warm(warmed, path, otherPayload);
        List<byte[]> largePayloads = [];
        for (int i = 0; i < LargeRecordCount; i++)
        {
            byte[] largePayload = PbtGroupRecordBufferTests.Payload(i, 400);
            PbtSnapshotTestExtensions.Warm(warmed, Path(1000 + i), largePayload);
            largePayloads.Add(largePayload);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Get(warmed, path), Is.EqualTo(otherPayload), "a later group replaces the path");
            Assert.That(Enumerable.Range(0, LargeRecordCount).Select(i => Get(warmed, Path(1000 + i))), Is.EqualTo(largePayloads), "records in every generation");
        }

        warmed.Reset();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warmed.Count, Is.Zero, "count after reset");
            Assert.That(Get(warmed, path), Is.Null, "nothing remains");
            Assert.That(Enumerable.Range(0, LargeRecordCount).Select(i => Get(warmed, Path(1000 + i))), Is.All.Null, "nothing remains");
        }

        ulong resetPointer = PbtSnapshotTestExtensions.Warm(warmed, path, payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Get(warmed, path), Is.EqualTo(payload), "reusable after reset");
            Assert.That(resetPointer, Is.Not.EqualTo(pointer), "writes continue past the records before the reset");
        }
    }

    [Test]
    public void Concurrent_writes_publish_complete_records([Values] bool samePosition)
    {
        const int count = 1000;
        using PbtWarmedGroupBuffer warmed = new(1);
        byte[] payload = PbtGroupRecordBufferTests.Payload(0, 400);
        bool[] complete = new bool[count];

        Parallel.For(0, count, i =>
        {
            PbtStorageNodePath path = Path(samePosition ? 0 : i);
            PbtSnapshotTestExtensions.Warm(warmed, path, payload);
            complete[i] = warmed.TryGet(path, out PbtGroupRecord current) && current.Payload.Span.SequenceEqual(payload) && current.Path.Equals(path);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(complete, Is.All.True);
            Assert.That(warmed.Count, Is.EqualTo(samePosition ? 1 : count));
        }
    }

    public static PbtStorageNodePath Path(int seed) => PbtTestPaths.Prefix<PbtStorageNodePath>(ValueKeccak.Compute(BitConverter.GetBytes(seed)).Bytes, 255);

    private static byte[]? Get(PbtWarmedGroupBuffer warmed, in PbtStorageNodePath path) =>
        warmed.TryGet(path, out PbtGroupRecord record) ? record.Payload.ToArray() : null;
}
