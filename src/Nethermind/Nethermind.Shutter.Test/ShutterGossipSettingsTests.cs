// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.PubsubPeerDiscovery;
using Nethermind.Shutter.Config;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Shutter.Test;

public class ShutterGossipSettingsTests
{
    [TestCase("/ip4/10.0.0.1/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", true, TestName = "An IPv4 address")]
    [TestCase("/dns4/sequencer.example/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", true, TestName = "A DNS name")]
    [TestCase("/dnsaddr/sequencer.example/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", true, TestName = "A dnsaddr name")]
    [TestCase("/ip4/10.0.0.1/tcp/9222/dnsaddr/sequencer.example/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", false, TestName = "A dnsaddr name after an address")]
    [TestCase("/ip4/10.0.0.1/udp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", false, TestName = "A transport other than TCP")]
    [TestCase("/ip4/10.0.0.1/tcp/9222", false, TestName = "No peer id")]
    [TestCase("not a multiaddr", false, TestName = "Not a multiaddr")]
    public void Bootnodes_are_checked_at_startup(string bootnode, bool dialable)
    {
        ShutterConfig config = new()
        {
            ValidatorRegistryContractAddress = Address.Zero.ToString(),
            KeyBroadcastContractAddress = Address.Zero.ToString(),
            KeyperSetManagerContractAddress = Address.Zero.ToString(),
            SequencerContractAddress = Address.Zero.ToString(),
            Validator = false,
            BootnodeP2PAddresses = ["/ip4/10.0.0.2/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", bootnode],
        };
        IShutterApi api = Substitute.For<IShutterApi>();
        IEnumerable<Multiaddress>? started = null;
        api.StartP2P(Arg.Do<IEnumerable<Multiaddress>>(bootnodes => started = bootnodes), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        TestLogger logger = new();

        new RunShutterP2P(config, api, Substitute.For<IProcessExitSource>(), new OneLoggerLogManager(new ILogger(logger))).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(started?.Select(static address => address.ToString()), Is.EqualTo(dialable ? new[] { "/ip4/10.0.0.2/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", bootnode } : ["/ip4/10.0.0.2/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW"]));
            Assert.That(logger.LogList.Where(line => line.Contains(bootnode)), dialable ? Is.Empty : Has.Exactly(1).Contains("is skipped"));
        }
    }

    // A delivery score would prune peers on quiet topics as under-delivering.
    [Test]
    public void Key_and_discovery_gossip_is_unscored()
    {
        PubsubPeerDiscoverySettings discovery = new();
        PubsubSettings settings = ShutterP2P.CreatePubsubSettings(discovery);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.TopicScoreParams["decryptionKeys"].TopicWeight, Is.Zero);
            Assert.That(discovery.Topics, Has.All.Matches<string>(topic => settings.TopicScoreParams[topic].TopicWeight == 0));
            Assert.That(settings.BehaviorPenaltyWeight, Is.Zero);
            Assert.That(settings.IPColocationFactorWeight, Is.Zero);
        }
    }
}
