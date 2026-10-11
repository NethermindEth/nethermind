// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Google.Protobuf;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Optimism.CL;
using Nethermind.Core.Extensions;
using Nethermind.Libp2p.Core;
using Nethermind.Network.Libp2p;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Optimism.CL.P2P;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class OptimismGossipSettingsTests
{
    private const string BlocksTopic = "/optimism/10/2/blocks";

    [TestCase("/ip4/10.0.0.1/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", true, TestName = "An IPv4 address")]
    [TestCase("/dns4/sequencer.example/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", true, TestName = "A DNS name")]
    [TestCase("/dnsaddr/sequencer.example/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", true, TestName = "A dnsaddr name")]
    [TestCase("/ip4/10.0.0.1/tcp/9222/dnsaddr/sequencer.example/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", false, TestName = "A dnsaddr name after an address")]
    [TestCase("/ip4/10.0.0.1/udp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", false, TestName = "A transport other than TCP")]
    [TestCase("/ip4/10.0.0.1/tcp/9222", false, TestName = "No peer id")]
    [TestCase("not a multiaddr", false, TestName = "Not a multiaddr")]
    public void Static_peers_are_checked_at_startup(string node, bool dialable)
    {
        TestLogger logger = new();
        using OptimismCLP2P p2p = new(Substitute.For<IExecutionEngineManager>(), 10, ["/ip4/10.0.0.2/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", node], new OptimismConfig(), Address.Zero,
            Substitute.For<ITimestamper>(), Substitute.For<IIPResolver>(), new OneLoggerLogManager(new ILogger(logger)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(p2p.StaticPeersForTest.Select(static peer => peer.ToString()), Is.EqualTo(dialable ? new[] { "/ip4/10.0.0.2/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", node } : ["/ip4/10.0.0.2/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW"]));
            Assert.That(logger.LogList.Where(line => line.Contains(node)), dialable ? Is.Empty : Has.Exactly(1).Contains("is skipped"));
        }
    }

    // Expected IDs hash domain, little-endian topic length, topic, and decoded or raw data, taking the first 20 bytes.
    [TestCase("0x051068656c6c6f", "0x0089d50f80a1e1668e93fef3bd0d075261438c71", TestName = "Valid snappy hashes the decoded data")]
    [TestCase("0xffffffff", "0xd8920a2d23d659830adbab71431e706fbba03ab8", TestName = "Invalid snappy hashes the raw data")]
    public void Message_id_is_the_op_stack_gossip_id(string data, string expected)
    {
        PubsubSettings settings = OptimismCLP2P.CreatePubsubSettings(BlocksTopic);

        MessageId id = settings.GetMessageId(new Message { Topic = BlocksTopic, Data = ByteString.CopyFrom(Bytes.FromHexString(data)) });

        Assert.That(id.Bytes, Is.EqualTo(Bytes.FromHexString(expected)));
    }

    [Test]
    public void Block_gossip_uses_op_stack_limits_and_is_unscored()
    {
        PubsubSettings settings = OptimismCLP2P.CreatePubsubSettings(BlocksTopic);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.MaxRpcBytes, Is.EqualTo(Eth2MessageId.MaxMessageSize).And.GreaterThan(10 * 1024 * 1024));
            Assert.That(settings.MaxIwantResponseBytes, Is.EqualTo(Eth2MessageId.MaxMessageSize));
            Assert.That(settings.TopicScoreParams[BlocksTopic].TopicWeight, Is.Zero);
            Assert.That(settings.BehaviorPenaltyWeight, Is.Zero);
            Assert.That(settings.IPColocationFactorWeight, Is.Zero);
        }
    }
}
