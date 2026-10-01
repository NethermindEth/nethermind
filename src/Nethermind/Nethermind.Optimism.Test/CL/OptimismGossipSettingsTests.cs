// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Google.Protobuf;
using Nethermind.Core.Extensions;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Optimism.CL.P2P;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class OptimismGossipSettingsTests
{
    private const string BlocksTopic = "/optimism/10/2/blocks";

    /// <summary>The ids the network's IHAVE lists carry: an id that differs is never matched, so every promise to the peer that sent it breaks.</summary>
    /// <remarks>Expected values hash 0x01000000 or 0x00000000, the little-endian topic length, the topic and the snappy-decoded or raw data, first 20 bytes.</remarks>
    [TestCase("0x051068656c6c6f", "0x0089d50f80a1e1668e93fef3bd0d075261438c71", TestName = "Valid snappy hashes the decoded data")]
    [TestCase("0xffffffff", "0xd8920a2d23d659830adbab71431e706fbba03ab8", TestName = "Invalid snappy hashes the raw data")]
    public void Message_id_is_the_op_stack_gossip_id(string data, string expected)
    {
        PubsubSettings settings = OptimismCLP2P.CreatePubsubSettings(BlocksTopic);

        MessageId id = settings.GetMessageId(new Message { Topic = BlocksTopic, Data = ByteString.CopyFrom(Bytes.FromHexString(data)) });

        Assert.That(id.Bytes, Is.EqualTo(Bytes.FromHexString(expected)));
    }

    /// <summary>No gossip delivery, unanswered IWANT or shared address moves an op-node peer's score.</summary>
    [Test]
    public void Block_gossip_is_unscored()
    {
        PubsubSettings settings = OptimismCLP2P.CreatePubsubSettings(BlocksTopic);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.TopicScoreParams[BlocksTopic].TopicWeight, Is.Zero);
            Assert.That(settings.BehaviorPenaltyWeight, Is.Zero);
            Assert.That(settings.IPColocationFactorWeight, Is.Zero);
        }
    }
}
