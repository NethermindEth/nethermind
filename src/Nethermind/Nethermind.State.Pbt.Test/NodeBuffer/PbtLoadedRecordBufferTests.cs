// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test.NodeBuffer;

public class PbtLoadedRecordBufferTests
{
    private const int GroupCount = 200;
    private const int PayloadLength = 1024;
    // Larger than a chunk, so it gets a chunk of its own.
    private const int OversizedPayloadLength = 100 * 1024;

    [Test]
    public void Loads_payloads_or_records_into_records_that_stay_readable([Values] bool asRecord)
    {
        byte[][] payloads = [.. Enumerable.Range(0, GroupCount).Select(i => Payload(i, i == GroupCount / 2 ? OversizedPayloadLength : PayloadLength))];
        Store store = new(payloads.Select((payload, i) => (PbtWarmedGroupBufferTests.Path(i), payload)), asRecord);
        using PbtLoadedRecordBuffer loaded = new();
        PbtGroupRecord[] records = new PbtGroupRecord[GroupCount];
        bool[] found = new bool[GroupCount];
        for (int i = 0; i < GroupCount; i++) found[i] = TryLoad(loaded, store, PbtWarmedGroupBufferTests.Path(i), out records[i]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.All.True);
            Assert.That(records.Select(static record => record.Payload.ToArray()), Is.EqualTo(payloads), "records stay readable as later ones fill new chunks");
            Assert.That(records.Select(static record => record.Path), Is.EqualTo(Enumerable.Range(0, GroupCount).Select(PbtWarmedGroupBufferTests.Path)));
            Assert.That(records.SelectMany(static record => Enumerable.Range(0, PbtTwoLevelGroupGeometry.BoundarySlots).Select(record.GetLink)), Is.All.Zero, "a loaded group links to no descendant");
            Assert.That(TryLoad(loaded, store, PbtWarmedGroupBufferTests.Path(GroupCount), out _), Is.False, "a store miss");
        }
    }

    private static bool TryLoad(PbtLoadedRecordBuffer loaded, Store store, PbtStorageNodePath path, out PbtGroupRecord record) =>
        loaded.TryLoad(store, PbtTraversalPath.FromPath(stackalloc byte[PbtVariableTreeKey.MaxLength], path), out record);

    /// <summary>A distinct group payload, starting with the group header as a payload does.</summary>
    private static byte[] Payload(int seed, int length)
    {
        byte[] payload = new byte[length];
        PbtNodeGroupCodec.Header.CopyTo(payload);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(PbtNodeGroupCodec.HeaderLength), seed);
        return payload;
    }

    /// <summary>Hands over each group's payload, or its record when <paramref name="asRecord"/> is set, as a store holding records does.</summary>
    private sealed class Store(IEnumerable<(PbtStorageNodePath Path, byte[] Payload)> groups, bool asRecord) : IPbtStore
    {
        private readonly Dictionary<PbtStorageNodePath, byte[]> _groups = groups.ToDictionary(group => group.Path, group => asRecord ? Record(group.Path, group.Payload) : group.Payload);

        public bool TryLocate(ulong pointer, out PbtGroupRecord record) => throw new NotSupportedException();

        public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => throw new NotSupportedException();

        public bool TryLoad<TArg>(scoped in PbtTraversalPath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            if (!_groups.TryGetValue(path.ToPath<PbtStorageNodePath>(), out byte[]? group)) return false;

            action(group, arg);
            return true;
        }

        private static byte[] Record(in PbtStorageNodePath path, byte[] payload)
        {
            byte[] record = new byte[PbtGroupRecordCodec.Length(payload.Length)];
            payload.CopyTo(PbtGroupRecordCodec.WriteGroup(record, path, default, payload.Length));
            return record;
        }
    }
}
