// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;

[Parallelizable(ParallelScope.All)]
public class GetBlockHeadersMessageSerializerTests
{
    [Test]
    public void Roundtrip_hash()
    {
        using GetBlockHeadersMessage message = new();
        message.MaxHeaders = 1;
        message.Skip = 2;
        message.Reverse = 1;
        message.StartBlockHash = Keccak.OfAnEmptyString;
        GetBlockHeadersMessageSerializer serializer = new();
        byte[] bytes = serializer.Serialize(message);
        byte[] expectedBytes = Bytes.FromHexString("e4a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470010201");

        Assert.That(Bytes.AreEqual(bytes, expectedBytes), Is.True, "bytes");

        using GetBlockHeadersMessage deserialized = serializer.Deserialize(bytes);
        Assert.That(deserialized.StartBlockHash, Is.EqualTo(message.StartBlockHash), $"{nameof(message.StartBlockHash)}");
        Assert.That(deserialized.MaxHeaders, Is.EqualTo(message.MaxHeaders), $"{nameof(message.MaxHeaders)}");
        Assert.That(deserialized.Reverse, Is.EqualTo(message.Reverse), $"{nameof(message.Reverse)}");
        Assert.That(deserialized.Skip, Is.EqualTo(message.Skip), $"{nameof(message.Skip)}");

        SerializerTester.TestZero(serializer, message);
    }

    [Test]
    public void Roundtrip_number()
    {
        using GetBlockHeadersMessage message = new();
        message.MaxHeaders = 1;
        message.Skip = 2;
        message.Reverse = 1;
        message.StartBlockNumber = 100;
        GetBlockHeadersMessageSerializer serializer = new();
        byte[] bytes = serializer.Serialize(message);
        byte[] expectedBytes = Bytes.FromHexString("c464010201");

        Assert.That(Bytes.AreEqual(bytes, expectedBytes), Is.True, "bytes");

        using GetBlockHeadersMessage deserialized = serializer.Deserialize(bytes);
        Assert.That(deserialized.StartBlockNumber, Is.EqualTo(message.StartBlockNumber), $"{nameof(message.StartBlockNumber)}");
        Assert.That(deserialized.MaxHeaders, Is.EqualTo(message.MaxHeaders), $"{nameof(message.MaxHeaders)}");
        Assert.That(deserialized.Reverse, Is.EqualTo(message.Reverse), $"{nameof(message.Reverse)}");
        Assert.That(deserialized.Skip, Is.EqualTo(message.Skip), $"{nameof(message.Skip)}");

        SerializerTester.TestZero(serializer, message);
    }

    [Test]
    public void Roundtrip_zero()
    {
        using GetBlockHeadersMessage message = new();
        message.MaxHeaders = 1;
        message.Skip = 2;
        message.Reverse = 0;
        message.StartBlockNumber = 100;
        GetBlockHeadersMessageSerializer serializer = new();

        byte[] bytes = serializer.Serialize(message);
        byte[] expectedBytes = Bytes.FromHexString("c464010280");

        Assert.That(bytes, Is.EqualTo(expectedBytes), "bytes");

        using GetBlockHeadersMessage deserialized = serializer.Deserialize(bytes);
        Assert.That(deserialized.StartBlockNumber, Is.EqualTo(message.StartBlockNumber), $"{nameof(message.StartBlockNumber)}");
        Assert.That(deserialized.MaxHeaders, Is.EqualTo(message.MaxHeaders), $"{nameof(message.MaxHeaders)}");
        Assert.That(deserialized.Reverse, Is.EqualTo(message.Reverse), $"{nameof(message.Reverse)}");
        Assert.That(deserialized.Skip, Is.EqualTo(message.Skip), $"{nameof(message.Skip)}");

        SerializerTester.TestZero(serializer, message);
    }

    [Test]
    public void To_string()
    {
        using GetBlockHeadersMessage newBlockMessage = new();
        _ = newBlockMessage.ToString();
    }

    [Test]
    public void Deserialize_allows_32_byte_start_block_selector()
    {
        GetBlockHeadersMessageSerializer serializer = new();
        byte[] bytes = BuildSerializedMessageWithStartBlockSelector(Hash256.Size);

        using GetBlockHeadersMessage deserialized = serializer.Deserialize(bytes);
        Assert.That(deserialized.StartBlockHash, Is.Not.Null);
        Assert.That(deserialized.MaxHeaders, Is.EqualTo(1));
        Assert.That(deserialized.Skip, Is.EqualTo(2));
        Assert.That(deserialized.Reverse, Is.EqualTo(1));
    }

    [Test]
    public void Deserialize_throws_on_start_block_selector_above_32_bytes()
    {
        GetBlockHeadersMessageSerializer serializer = new();
        byte[] bytes = BuildSerializedMessageWithStartBlockSelector(Hash256.Size + 1);

        Assert.Throws<RlpLimitException>(() => serializer.Deserialize(bytes));
    }

    private static byte[] BuildSerializedMessageWithStartBlockSelector(int startBlockLength)
    {
        byte[] startBlockSelector = new byte[startBlockLength];
        for (int i = 0; i < startBlockSelector.Length; i++)
        {
            startBlockSelector[i] = (byte)(i + 1);
        }

        byte[] encodedStartBlockSelector = new byte[startBlockSelector.Length + 1];
        encodedStartBlockSelector[0] = (byte)(0x80 + startBlockSelector.Length);
        Buffer.BlockCopy(startBlockSelector, 0, encodedStartBlockSelector, 1, startBlockSelector.Length);

        byte[] serialized = new byte[1 + encodedStartBlockSelector.Length + 3];
        serialized[0] = (byte)(0xc0 + encodedStartBlockSelector.Length + 3);
        Buffer.BlockCopy(encodedStartBlockSelector, 0, serialized, 1, encodedStartBlockSelector.Length);
        serialized[^3] = 0x01;
        serialized[^2] = 0x02;
        serialized[^1] = 0x01;

        return serialized;
    }
}
