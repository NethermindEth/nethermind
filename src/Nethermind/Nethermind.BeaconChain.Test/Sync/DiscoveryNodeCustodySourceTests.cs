// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Enr;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// The importer's custody must be derived from the very node id discovery advertises, or the columns
/// this node demands of a block and the columns it tells peers it custodies drift apart silently.
/// </summary>
public class DiscoveryNodeCustodySourceTests
{
    [Test]
    public async Task Custody_is_derived_from_the_node_id_peers_read_off_the_local_enr()
    {
        await using BeaconDiscovery discovery = NewDiscovery(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()));
        // Resolves the identity and local custody exactly as Start does, without binding a socket.
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        Hash256 enrNodeId = discovery.LocalNodeRecord.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)!.Decompress().Hash;

        NodeColumnCustody demanded = new DiscoveryNodeCustodySource(discovery).Current!;

        Assert.That(demanded.NodeId, Is.EqualTo(enrNodeId),
            "peers compute this node's custody from the secp256k1 key in its ENR; the columns demanded here must be that identity's");
    }

    [Test]
    public async Task Custody_is_re_derived_when_discovery_advertises_a_different_identity()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        await using BeaconDiscovery discovery = NewDiscovery(store);
        DiscoveryNodeCustodySource source = new(discovery);
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        NodeColumnCustody first = source.Current!;

        // Discovery loads whatever identity the store holds, so a replaced key resolves to a new node id.
        store.PutMetadata(BeaconDiscovery.IdentityMetadataKey, TestItem.PrivateKeyB.KeyBytes);
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        Assert.That(discovery.LocalCustody.NodeId, Is.Not.EqualTo(first.NodeId), "the identity swap the test relies on did not take");

        NodeColumnCustody second = source.Current!;

        Assert.Multiple(() =>
        {
            Assert.That(second.NodeId, Is.EqualTo(discovery.LocalCustody.NodeId), "the custody demanded must follow the identity discovery currently advertises");
            Assert.That(second.CustodyColumns, Is.Not.EqualTo(first.CustodyColumns), "a stale custody would demand the previous identity's columns");
            Assert.That(source.Current, Is.SameAs(second), "an unchanged identity is not re-derived on every read");
        });
    }

    [Test]
    public void Custody_columns_demanded_by_the_importer_are_the_columns_advertised_to_peers()
    {
        Hash256 nodeId = TestItem.PrivateKeyB.PublicKey.Hash;
        LocalCustody advertised = new(nodeId, Eip7594DasConstants.CustodyRequirement);
        NodeColumnCustody demanded = new(nodeId, advertised.CustodyGroupCount);

        Assert.Multiple(() =>
        {
            Assert.That(demanded.CustodyColumns, Is.EqualTo(advertised.CustodyGroups.SelectMany(CustodyGroups.ComputeColumnsForCustodyGroup).Order()));
            Assert.That(demanded.CustodyColumns, Is.SubsetOf(demanded.SampledColumns), "the per-slot sample always covers the node's own custody");
            Assert.That(demanded.SampledColumns, Has.Count.EqualTo((int)Eip7594DasConstants.SamplesPerSlot));
        });
    }

    [Test]
    public void Without_discovery_there_is_no_identity() =>
        Assert.That(new DiscoveryNodeCustodySource(null).Current, Is.Null);

    private static BeaconDiscovery NewDiscovery(BeaconChainStore store) =>
        new(new BeaconChainConfig { Discv5Port = 0 }, BeaconChainSpec.Mainnet, store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);

    private sealed class FixedIPResolver(IPAddress ip) : IIPResolver
    {
        public ValueTask<IIPResolver.NethermindIp> Resolve(CancellationToken cancellationToken = default) =>
            new(new IIPResolver.NethermindIp(ip, ip));
    }
}
