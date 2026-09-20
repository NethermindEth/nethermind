// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Network;
using NSubstitute;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.Discovery;

/// <summary>
/// The spec having bootnodes is not the same as discovery dialing them: the two were wired through
/// a separate chain-id switch that drifted, leaving a supported network discovery could not start on.
/// </summary>
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
    public void Discovery_dials_the_networks_own_bootnodes_when_the_config_does_not_override_them()
    {
        List<Node> nodes = Discovery(BeaconChainSpec.Mainnet).CreateBootNodes();

        Assert.That(nodes, Is.Not.Empty, "mainnet discovery has no bootnode to dial");
    }

    [Test]
    public void A_configured_bootnode_list_replaces_the_networks_own()
    {
        List<Node> fromSpec = Discovery(BeaconChainSpec.Mainnet).CreateBootNodes();
        List<Node> overridden = Discovery(BeaconChainSpec.Mainnet, BeaconChainSpec.Hoodi.Bootnodes[0]).CreateBootNodes();

        Assert.Multiple(() =>
        {
            Assert.That(overridden, Has.Count.EqualTo(1));
            Assert.That(overridden[0].Id, Is.Not.EqualTo(fromSpec[0].Id));
        });
    }
}
