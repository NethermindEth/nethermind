// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Network;
using NSubstitute;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace Nethermind.BeaconChain.Test.P2P.Discovery;

public class BeaconDiscoveryBootnodesTests
{
    private static BeaconDiscovery Discovery(BeaconChainSpec spec, string? configBootnodes = null)
    {
        IIPResolver ipResolver = Substitute.For<IIPResolver>();
        ipResolver.Resolve(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IIPResolver.NethermindIp>(new IIPResolver.NethermindIp(IPAddress.Loopback, IPAddress.Loopback)));

        return new BeaconDiscovery(
            new BeaconChainConfig { Bootnodes = configBootnodes! },
            spec,
            new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()),
            ipResolver,
            Timestamper.Default,
            LimboLogs.Instance);
    }

    [Test]
    public void Discovery_dials_the_networks_own_bootnodes_when_the_config_does_not_override_them(
        [Values(BlockchainIds.Mainnet, BlockchainIds.Hoodi, BlockchainIds.Sepolia)] ulong chainId)
    {
        BeaconChainSpec spec = BeaconChainSpec.ForChainId(chainId);
        PublicKey[] own = [.. Discovery(spec).CreateBootNodes().Select(static n => n.Id)];
        // The same records listed explicitly, so only the network's own list can match.
        PublicKey[] listed = [.. Discovery(BeaconChainSpec.Mainnet, string.Join(',', spec.Bootnodes)).CreateBootNodes().Select(static n => n.Id)];

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(own, Is.Not.Empty, $"chain {chainId} discovery has no bootnode to dial");
        Assert.That(own, Is.EquivalentTo(listed), $"chain {chainId} dials bootnodes other than its own");
    }

    [Test]
    public void A_configured_bootnode_list_replaces_the_networks_own()
    {
        List<Node> fromSpec = Discovery(BeaconChainSpec.Mainnet).CreateBootNodes();
        List<Node> overridden = Discovery(BeaconChainSpec.Mainnet, BeaconChainSpec.Hoodi.Bootnodes[0]).CreateBootNodes();

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(overridden, Has.Count.EqualTo(1));
        Assert.That(overridden[0].Id, Is.Not.EqualTo(fromSpec[0].Id));
    }
}
