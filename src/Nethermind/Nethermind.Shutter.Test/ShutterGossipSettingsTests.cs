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
    private static readonly (string? Address, bool Dialable)[] Bootnodes =
    [
        ("/ip4/10.0.0.1/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", true),
        ("/dns4/sequencer.example/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", true),
        ("/dnsaddr/sequencer.example/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", true),
        ("/ip4/10.0.0.1/tcp/9222/dnsaddr/sequencer.example/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", false),
        ("/ip4/10.0.0.1/udp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", false),
        ("/ip4/10.0.0.1/tcp/9222", false),
        ("not a multiaddr", false),
        ("/ip4/127.0.0.1/udp/9222", false),
        (null, true),
    ];

    [Test]
    public void Bootnodes_are_checked_at_startup(
        [ValueSource(nameof(Bootnodes))] (string? Address, bool Dialable) input, [Values] bool mixed)
    {
        (string? bootnode, bool dialable) = input;
        ShutterConfig config = new()
        {
            ValidatorRegistryContractAddress = Address.Zero.ToString(),
            KeyBroadcastContractAddress = Address.Zero.ToString(),
            KeyperSetManagerContractAddress = Address.Zero.ToString(),
            SequencerContractAddress = Address.Zero.ToString(),
            Validator = false,
            BootnodeP2PAddresses = bootnode is null ? [] : [bootnode],
        };
        if (mixed)
            config.BootnodeP2PAddresses = ["/ip4/10.0.0.1/tcp/9222/p2p/16Uiu2HAmRvz3gCpQuMeRxEz1F8B8EXHE9q9V1VE6pMVQuRWUt2iW", .. config.BootnodeP2PAddresses];
        IShutterApi api = Substitute.For<IShutterApi>();
        IEnumerable<Multiaddress>? started = null;
        api.StartP2P(Arg.Do<IEnumerable<Multiaddress>>(bootnodes => started = bootnodes), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        TestLogger logger = new();

        using CancellationTokenSource stop = new();
        IProcessExitSource exit = Substitute.For<IProcessExitSource>();
        exit.Token.Returns(stop.Token);
        RunShutterP2P step = new(config, api, exit, new OneLoggerLogManager(new ILogger(logger)));
        Assert.That(() => step.Execute(CancellationToken.None), dialable ? Throws.Nothing
            : Throws.TypeOf<ShutterPlugin.ShutterLoadingException>().With.InnerException.Message.Contains("BootnodeP2PAddresses"));
        api.Received(dialable ? 1 : 0).StartP2P(Arg.Any<IEnumerable<Multiaddress>>(), stop.Token);
        if (!dialable) api.DidNotReceive().StartP2P(Arg.Any<IEnumerable<Multiaddress>>(), Arg.Any<CancellationToken>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(started?.Select(static address => address.ToString()), Is.EqualTo(dialable ? config.BootnodeP2PAddresses : null));
            Assert.That(logger.LogList.Where(line => line.Contains("is skipped")), dialable ? Is.Empty : Has.Exactly(1).Contains(bootnode!));
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
