// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;

[Parallelizable(ParallelScope.All)]
public class GetBlockBodiesMessageSerializerTests
{
    [Test]
    public void Roundtrip()
    {
        GetBlockBodiesMessageSerializer serializer = new();
        using GetBlockBodiesMessage message = new(Keccak.OfAnEmptySequenceRlp, Keccak.Zero, Keccak.EmptyTreeHash);
        byte[] bytes = serializer.Serialize(message);
        byte[] expectedBytes = Bytes.FromHexString("f863a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347a00000000000000000000000000000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421");

        Assert.That(Bytes.AreEqual(bytes, expectedBytes), Is.True, "bytes");

        using GetBlockBodiesMessage deserialized = serializer.Deserialize(bytes);
        Assert.That(deserialized.BlockHashes.Count, Is.EqualTo(message.BlockHashes.Count), $"count");
        for (int i = 0; i < message.BlockHashes.Count; i++)
        {
            Assert.That(deserialized.BlockHashes[i], Is.EqualTo(message.BlockHashes[i]), $"hash {i}");
        }

        SerializerTester.TestZero(serializer, message);
    }

    [TestCase(500)]
    [TestCase(1024)]
    public void Can_deserialize_body_request_up_to_message_limit(int hashCount)
    {
        GetBlockBodiesMessageSerializer serializer = new();
        using GetBlockBodiesMessage message = new(CreateHashes(hashCount));
        byte[] bytes = serializer.Serialize(message);

        using GetBlockBodiesMessage deserialized = serializer.Deserialize(bytes);

        Assert.That(deserialized.BlockHashes, Has.Count.EqualTo(hashCount));
    }

    [Test]
    public void Rejects_body_request_above_message_limit()
    {
        GetBlockBodiesMessageSerializer serializer = new();
        using GetBlockBodiesMessage message = new(CreateHashes(1025));
        byte[] bytes = serializer.Serialize(message);

        Assert.Throws<RlpLimitException>(() => serializer.Deserialize(bytes));
    }

    [Test]
    public void To_string()
    {
        using GetBlockBodiesMessage newBlockMessage = new();
        _ = newBlockMessage.ToString();
    }

    private static Hash256[] CreateHashes(int count)
    {
        Hash256[] hashes = new Hash256[count];
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = Keccak.Zero;
        }

        return hashes;
    }
}
