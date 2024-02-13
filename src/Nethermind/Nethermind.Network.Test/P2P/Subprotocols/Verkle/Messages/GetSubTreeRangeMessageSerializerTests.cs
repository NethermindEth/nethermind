// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Verkle.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Verkle.Messages;

public class GetSubTreeRangeMessageSerializerTests
{
    [Test]
    public void Roundtrip()
    {
        GetSubTreeRangeMessage msg = new()
        {
            RequestId = MessageConstants.Random.NextLong(),
            SubTreeRange = new(Keccak.OfAnEmptyString, TestItem.Stem1, TestItem.Stem2),
            ResponseBytes = 10
        };
        GetSubTreeRangeMessageSerializer serializer = new();

        var bytes = serializer.Serialize(msg);
        var deserializedMsg = serializer.Deserialize(bytes);

        Assert.That(deserializedMsg.RequestId, Is.EqualTo(msg.RequestId));
        Assert.That(deserializedMsg.PacketType, Is.EqualTo(msg.PacketType));
        Assert.That(deserializedMsg.SubTreeRange.RootHash, Is.EqualTo(msg.SubTreeRange.RootHash));
        Assert.That(deserializedMsg.SubTreeRange.StartingStem, Is.EqualTo(msg.SubTreeRange.StartingStem));
        Assert.That(deserializedMsg.SubTreeRange.LimitStem, Is.EqualTo(msg.SubTreeRange.LimitStem));
        Assert.That(deserializedMsg.ResponseBytes, Is.EqualTo(msg.ResponseBytes));

        SerializerTester.TestZero(serializer, msg);
    }
}
