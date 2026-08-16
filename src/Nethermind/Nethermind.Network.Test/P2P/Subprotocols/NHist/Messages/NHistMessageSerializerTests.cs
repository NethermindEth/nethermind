// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Reflection;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.NHist.Messages;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.NHist.Messages;

[Parallelizable(ParallelScope.All)]
public class NHistMessageSerializerTests
{
    [Test]
    public void GetChangesetsMessage_roundtrips()
    {
        GetChangesetsMessageSerializer serializer = new();
        using GetChangesetsMessage message = new() { RequestId = 1, FromBlock = 100, ToBlock = 200, ResponseBytes = 42 };

        byte[] serialized = serializer.Serialize(message);
        using GetChangesetsMessage deserialized = serializer.Deserialize(serialized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserialized.FromBlock, Is.EqualTo(message.FromBlock));
            Assert.That(deserialized.ToBlock, Is.EqualTo(message.ToBlock));
            Assert.That(deserialized.ResponseBytes, Is.EqualTo(message.ResponseBytes));
        }
    }

    [Test]
    public void ChangesetsMessage_roundtrips()
    {
        ChangesetsMessageSerializer serializer = new();
        ArrayPoolList<ChangesetChunkEntry> chunks = new(1) { new ChangesetChunkEntry(50, 0, true, new byte[] { 1, 2 }) };
        using ChangesetsMessage message = new() { RequestId = 9, Chunks = chunks };

        byte[] serialized = serializer.Serialize(message);
        using ChangesetsMessage deserialized = serializer.Deserialize(serialized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserialized.Chunks.Count, Is.EqualTo(1));
            Assert.That(deserialized.Chunks[0].Block, Is.EqualTo(50UL));
            Assert.That(deserialized.Chunks[0].IsLastChunkForBlock, Is.True);
            Assert.That(deserialized.Chunks[0].Payload.ToArray(), Is.EqualTo(new byte[] { 1, 2 }));
        }
    }

    [Test]
    public void NHistStatusMessage_roundtrips()
    {
        NHistStatusMessageSerializer serializer = new();
        using NHistStatusMessage message = new()
        {
            RequestId = 1,
            Scopes = [new HistoryServingScope(ValueKeccak.Zero, ValueKeccak.MaxValue, 10, 500)],
            SupportsFullClone = true,
            RowFormatVersion = 3
        };

        byte[] serialized = serializer.Serialize(message);
        using NHistStatusMessage deserialized = serializer.Deserialize(serialized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserialized.Scopes.Length, Is.EqualTo(1));
            Assert.That(deserialized.Scopes[0].FloorBlock, Is.EqualTo(10UL));
            Assert.That(deserialized.Scopes[0].WatermarkBlock, Is.EqualTo(500UL));
            Assert.That(deserialized.SupportsFullClone, Is.True);
            Assert.That(deserialized.RowFormatVersion, Is.EqualTo((byte)3));
        }
    }

    [Test]
    public void NHistStatusMessage_WindowedNode_roundtrips_SupportsFullClone_false()
    {
        NHistStatusMessageSerializer serializer = new();
        using NHistStatusMessage message = new() { RequestId = 1, SupportsFullClone = false, RowFormatVersion = 3 };

        byte[] serialized = serializer.Serialize(message);
        using NHistStatusMessage deserialized = serializer.Deserialize(serialized);

        Assert.That(deserialized.SupportsFullClone, Is.False, "a windowed node must advertise it cannot serve a full clone");
    }

    [Test]
    public void GetHistoryRowsMessage_roundtrips()
    {
        GetHistoryRowsMessageSerializer serializer = new();
        using GetHistoryRowsMessage message = new()
        {
            RequestId = 11,
            Column = HistoryRowColumn.StorageHistory,
            StartKey = [1, 2, 3],
            EndKey = [9, 9, 9],
            Cursor = [4, 5],
            ResponseBytes = 12345
        };

        byte[] serialized = serializer.Serialize(message);
        using GetHistoryRowsMessage deserialized = serializer.Deserialize(serialized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserialized.RequestId, Is.EqualTo(message.RequestId));
            Assert.That(deserialized.Column, Is.EqualTo(HistoryRowColumn.StorageHistory));
            Assert.That(deserialized.StartKey, Is.EqualTo(message.StartKey));
            Assert.That(deserialized.EndKey, Is.EqualTo(message.EndKey));
            Assert.That(deserialized.Cursor, Is.EqualTo(message.Cursor));
            Assert.That(deserialized.ResponseBytes, Is.EqualTo(message.ResponseBytes));
        }
    }

    [Test]
    public void HistoryRowsMessage_roundtrips()
    {
        HistoryRowsMessageSerializer serializer = new();
        ArrayPoolList<HistoryRowEntry> entries = new(2)
        {
            new HistoryRowEntry([1, 2, 3], new byte[] { 0xAA }),
            new HistoryRowEntry([4, 5, 6], Array.Empty<byte>())
        };
        using HistoryRowsMessage message = new() { RequestId = 4, Entries = entries, NextCursor = [7, 7], Refused = false };

        byte[] serialized = serializer.Serialize(message);
        using HistoryRowsMessage deserialized = serializer.Deserialize(serialized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserialized.Refused, Is.False);
            Assert.That(deserialized.Entries.Count, Is.EqualTo(2));
            Assert.That(deserialized.Entries[0].Key, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(deserialized.Entries[0].Value.ToArray(), Is.EqualTo(new byte[] { 0xAA }));
            Assert.That(deserialized.NextCursor, Is.EqualTo(message.NextCursor));
        }
    }

    [Test]
    public void HistoryRowsMessage_roundtrips_keys_that_share_a_prefix_with_the_previous_row()
    {
        HistoryRowsMessageSerializer serializer = new();
        ArrayPoolList<HistoryRowEntry> entries = new(4)
        {
            new HistoryRowEntry(StorageHistoryKey(slot: 1, block: 900), new byte[] { 0x11 }),
            new HistoryRowEntry(StorageHistoryKey(slot: 1, block: 400), new byte[] { 0x22 }),
            new HistoryRowEntry(StorageHistoryKey(slot: 1, block: 7), Array.Empty<byte>()),
            new HistoryRowEntry(StorageHistoryKey(slot: 2, block: 5), new byte[] { 0x33 }),
        };
        using HistoryRowsMessage message = new() { RequestId = 4, Entries = entries, NextCursor = null, Refused = false };

        byte[] serialized = serializer.Serialize(message);
        using HistoryRowsMessage deserialized = serializer.Deserialize(serialized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserialized.Entries.Count, Is.EqualTo(4));
            Assert.That(deserialized.Entries[0].Key, Is.EqualTo(StorageHistoryKey(slot: 1, block: 900)));
            Assert.That(deserialized.Entries[1].Key, Is.EqualTo(StorageHistoryKey(slot: 1, block: 400)),
                "a key encoded as a delta against its predecessor must rebuild byte for byte");
            Assert.That(deserialized.Entries[2].Key, Is.EqualTo(StorageHistoryKey(slot: 1, block: 7)));
            Assert.That(deserialized.Entries[3].Key, Is.EqualTo(StorageHistoryKey(slot: 2, block: 5)),
                "a row that starts a new flat key must still carry the bytes that differ");
            Assert.That(deserialized.Entries[2].Value.ToArray(), Is.Empty);
            Assert.That(deserialized.Entries[3].Value.ToArray(), Is.EqualTo(new byte[] { 0x33 }));
        }
    }

    [Test]
    public void HistoryRowsMessage_does_not_repeat_the_flat_key_of_every_version()
    {
        HistoryRowsMessageSerializer serializer = new();
        ArrayPoolList<HistoryRowEntry> repeated = new(4);
        ArrayPoolList<HistoryRowEntry> distinct = new(4);
        for (int i = 0; i < 4; i++)
        {
            byte[] unrelated = StorageHistoryKey(slot: 1, block: 100);
            unrelated[0] = (byte)(i + 1);
            repeated.Add(new HistoryRowEntry(StorageHistoryKey(slot: 1, block: (ulong)(100 - i)), new byte[] { 0x11 }));
            distinct.Add(new HistoryRowEntry(unrelated, new byte[] { 0x11 }));
        }

        using HistoryRowsMessage repeatedMessage = new() { RequestId = 1, Entries = repeated, NextCursor = null };
        using HistoryRowsMessage distinctMessage = new() { RequestId = 1, Entries = distinct, NextCursor = null };

        int repeatedLength = serializer.Serialize(repeatedMessage).Length;
        int distinctLength = serializer.Serialize(distinctMessage).Length;

        Assert.That(repeatedLength, Is.LessThan(distinctLength - 100),
            "four versions of one slot must cost far less on the wire than four different slots; without prefix encoding both weigh the same");
    }

    private static byte[] StorageHistoryKey(byte slot, ulong block)
    {
        // [4B account prefix | 32B slot | 16B account suffix | 8B block], the shape StorageHistory rows carry.
        byte[] key = new byte[52 + 8];
        key[0] = 0xAB;
        key[35] = slot;
        key[51] = 0xCD;
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(52), ~block);
        return key;
    }

    [Test]
    public void HistoryRowsMessage_Refused_roundtrips_true()
    {
        HistoryRowsMessageSerializer serializer = new();
        using HistoryRowsMessage message = new() { RequestId = 1, Refused = true, Entries = ArrayPoolList<HistoryRowEntry>.Empty(), NextCursor = null };

        byte[] serialized = serializer.Serialize(message);
        using HistoryRowsMessage deserialized = serializer.Deserialize(serialized);

        Assert.That(deserialized.Refused, Is.True, "a refused response must round-trip distinctly from an empty-but-served one");
    }

    [Test]
    public void GetHistoryRowsMessage_carries_no_height_or_block_range_field()
    {
        PropertyInfo[] properties = typeof(GetHistoryRowsMessage).GetProperties();
        bool hasHeightOrBlockRange = properties.Any(p => p.Name is "Height" or "FromBlock" or "ToBlock");

        Assert.That(hasHeightOrBlockRange, Is.False, "GetHistoryRows streams raw on-disk rows exactly as stored, every version - it has no as-of-height or block-range dimension");
    }

    [Test]
    public void No_message_shape_allows_combined_block_range_and_key_range_request()
    {
        PropertyInfo[] getChangesetsProperties = typeof(GetChangesetsMessage).GetProperties();
        bool changesetsHasKeyRange = getChangesetsProperties.Any(p => p.Name is "StartKey" or "EndKey");

        PropertyInfo[] getHistoryRowsProperties = typeof(GetHistoryRowsMessage).GetProperties();
        bool rowsHasBlockRange = getHistoryRowsProperties.Any(p => p.Name is "Height" or "FromBlock" or "ToBlock");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changesetsHasKeyRange, Is.False, "GetChangesetsMessage must stay a block-range-only request");
            Assert.That(rowsHasBlockRange, Is.False, "GetHistoryRowsMessage must stay a key-range-only request; letting either side gain the other's dimension would let one request force a scan of every version of every key in a range");
        }
    }
}
