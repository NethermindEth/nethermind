// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Test;
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
    public void Reads_values_that_stay_readable_until_cleared_lending_native_ones_in_place([Values] bool native)
    {
        byte[][] payloads = [.. Enumerable.Range(0, GroupCount).Select(i => Payload(i, i == GroupCount / 2 ? OversizedPayloadLength : PayloadLength))];
        TestMemDb store = native ? new NativeStore() : new TestMemDb();
        for (int i = 0; i < GroupCount; i++) store[Key(i)] = payloads[i];
        PbtLoadedRecordBuffer loaded = new();
        ReadOnlyMemory<byte>[] reads = new ReadOnlyMemory<byte>[GroupCount];
        bool[] found = new bool[GroupCount];
        for (int i = 0; i < GroupCount; i++) found[i] = loaded.TryRead(store, Key(i), out reads[i]);
        bool missing = loaded.TryRead(store, Key(GroupCount), out _);
        byte[][] readBack = [.. reads.Select(static read => read.ToArray())];
        bool[] inPlace = [.. reads.Select((read, i) => read.Span.Overlaps(payloads[i]))];
        int releasedBeforeClear = (store as NativeStore)?.Released ?? 0;
        loaded.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.All.True);
            Assert.That(missing, Is.False, "a store miss");
            Assert.That(readBack, Is.EqualTo(payloads), "values stay readable as later ones fill new chunks");
            Assert.That(inPlace, Is.All.EqualTo(native), "a native value is lent in place, any other is copied");
            Assert.That(releasedBeforeClear, Is.Zero, "a lent value stays pinned until cleared");
            Assert.That((store as NativeStore)?.Released ?? 0, Is.EqualTo(native ? GroupCount : 0), "clearing releases every pinned value once");
        }
    }

    private static byte[] Key(int index) => BitConverter.GetBytes(index);

    /// <summary>A distinct group payload, starting with the group header as a payload does, in memory the collector never moves.</summary>
    private static byte[] Payload(int seed, int length)
    {
        byte[] payload = GC.AllocateArray<byte>(length, pinned: true);
        PbtNodeGroupCodec.Header.CopyTo(payload);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(PbtNodeGroupCodec.HeaderLength), seed);
        return payload;
    }

    /// <summary>Lends its stored arrays as native slices, as RocksDB does, counting the released handles.</summary>
    private sealed class NativeStore : TestMemDb, IReadOnlyNativeKeyValueStore
    {
        public int Released { get; private set; }

        public ReadOnlySpan<byte> GetNativeSlice(scoped ReadOnlySpan<byte> key, out nint handle, ReadFlags flags = ReadFlags.None)
        {
            byte[]? value = Get(key, flags);
            handle = value is null ? 0 : 1;
            return value;
        }

        public void DangerousReleaseHandle(nint handle) => Released++;
    }
}
