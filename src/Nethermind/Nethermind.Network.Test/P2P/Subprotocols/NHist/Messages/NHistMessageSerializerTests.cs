// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    public void GetHistoryRangeAtHeightMessage_roundtrips()
    {
        GetHistoryRangeAtHeightMessageSerializer serializer = new();
        using GetHistoryRangeAtHeightMessage message = new()
        {
            RequestId = 7,
            StartKey = ValueKeccak.Zero,
            EndKey = ValueKeccak.MaxValue,
            Height = 123,
            Cursor = [1, 2, 3],
            ResponseBytes = 999_999
        };

        byte[] serialized = serializer.Serialize(message);
        using GetHistoryRangeAtHeightMessage deserialized = serializer.Deserialize(serialized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserialized.RequestId, Is.EqualTo(message.RequestId));
            Assert.That(deserialized.StartKey, Is.EqualTo(message.StartKey));
            Assert.That(deserialized.EndKey, Is.EqualTo(message.EndKey));
            Assert.That(deserialized.Height, Is.EqualTo(message.Height));
            Assert.That(deserialized.Cursor, Is.EqualTo(message.Cursor));
            Assert.That(deserialized.ResponseBytes, Is.EqualTo(message.ResponseBytes));
        }
    }

    [Test]
    public void HistoryRangeAtHeightMessage_roundtrips()
    {
        HistoryRangeAtHeightMessageSerializer serializer = new();
        ArrayPoolList<HistoryRangeEntry> entries = new(2)
        {
            new HistoryRangeEntry([1, 2, 3], 10, new byte[] { 0xAA, 0xBB }),
            new HistoryRangeEntry([4, 5, 6], 20, Array.Empty<byte>())
        };
        using HistoryRangeAtHeightMessage message = new() { RequestId = 3, Entries = entries, NextCursor = [9, 9] };

        byte[] serialized = serializer.Serialize(message);
        using HistoryRangeAtHeightMessage deserialized = serializer.Deserialize(serialized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserialized.RequestId, Is.EqualTo(message.RequestId));
            Assert.That(deserialized.Entries.Count, Is.EqualTo(2));
            Assert.That(deserialized.Entries[0].Key, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(deserialized.Entries[0].Block, Is.EqualTo(10UL));
            Assert.That(deserialized.Entries[0].Value.ToArray(), Is.EqualTo(new byte[] { 0xAA, 0xBB }));
            Assert.That(deserialized.Entries[1].Value.ToArray(), Is.Empty);
            Assert.That(deserialized.NextCursor, Is.EqualTo(message.NextCursor));
        }
    }

    [Test]
    public void HistoryRangeAtHeightMessage_WithNoNextCursor_roundtrips_as_null()
    {
        HistoryRangeAtHeightMessageSerializer serializer = new();
        using HistoryRangeAtHeightMessage message = new() { RequestId = 1, Entries = ArrayPoolList<HistoryRangeEntry>.Empty(), NextCursor = null };

        byte[] serialized = serializer.Serialize(message);
        using HistoryRangeAtHeightMessage deserialized = serializer.Deserialize(serialized);

        Assert.That(deserialized.NextCursor, Is.Null, "no continuation must roundtrip as a null cursor, not an empty-but-present one");
    }

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
            Scopes = [new HistoryServingScope(ValueKeccak.Zero, ValueKeccak.MaxValue, 10, 500)]
        };

        byte[] serialized = serializer.Serialize(message);
        using NHistStatusMessage deserialized = serializer.Deserialize(serialized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserialized.Scopes.Length, Is.EqualTo(1));
            Assert.That(deserialized.Scopes[0].FloorBlock, Is.EqualTo(10UL));
            Assert.That(deserialized.Scopes[0].WatermarkBlock, Is.EqualTo(500UL));
        }
    }

    [Test]
    public void No_message_shape_allows_combined_block_range_and_key_range_request()
    {
        PropertyInfo[] getHistoryRangeProperties = typeof(GetHistoryRangeAtHeightMessage).GetProperties();
        bool hasBlockRangeField = getHistoryRangeProperties.Any(p => p.Name is "FromBlock" or "ToBlock");

        PropertyInfo[] getChangesetsProperties = typeof(GetChangesetsMessage).GetProperties();
        bool hasKeyRangeField = getChangesetsProperties.Any(p => p.Name is "StartKey" or "EndKey");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hasBlockRangeField, Is.False, "GetHistoryRangeAtHeightMessage must stay a single-height, key-range-only request");
            Assert.That(hasKeyRangeField, Is.False, "GetChangesetsMessage must stay a block-range-only request; all key-range traffic goes through GetHistoryRangeAtHeight");
        }
    }
}
