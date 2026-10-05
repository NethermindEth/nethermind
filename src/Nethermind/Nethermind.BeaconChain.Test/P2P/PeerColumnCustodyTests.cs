// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Google.Protobuf;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using KeyType = Nethermind.Libp2p.Core.Dto.KeyType;
using Libp2pPublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// A remote peer serves only the columns it custodies (fulu/p2p-interface.md), so asking it for any other column
/// wastes the request and leaves the block unavailable. Its custody is <c>get_custody_groups</c> over its discv5
/// node id and advertised custody group count (fulu/das-core.md).
/// </summary>
public class PeerColumnCustodyTests
{
    /// <summary>The EIP-778 example record's private key, public key and node id.</summary>
    private const string Eip778PrivateKey = "b71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291";
    private const string Eip778PublicKey = "03ca634cae0d49acb401d8a4c6b6fe8c55b70d115bf400769cc1400f3258cd3138";
    private static readonly Hash256 Eip778NodeId = new("0xa448f24c6d18e575453db13171562b71999873db5b286df957af199ec94617f7");

    /// <summary>The raw id bytes <c>00..1f</c>, whose pyspec <c>get_custody_groups</c> results <see cref="Discovery.LocalCustodyTests"/> pins.</summary>
    private static readonly Hash256 KnownNodeId = new(Enumerable.Range(0, 32).Select(static i => (byte)i).ToArray());

    /// <summary>The key a peer's libp2p handshake carries is the compressed secp256k1 key of its ENR, so both name one node id.</summary>
    [Test]
    public void The_node_id_of_a_libp2p_secp256k1_key_is_its_discv5_node_id()
    {
        Libp2pPublicKey libp2pKey = new() { Type = KeyType.Secp256K1, Data = ByteString.CopyFrom(new CompressedPublicKey(Eip778PublicKey).Bytes) };
        Libp2pPublicKey ed25519Key = new() { Type = KeyType.Ed25519, Data = ByteString.CopyFrom(new byte[32]) };

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(PeerColumnCustody.NodeIdOf(libp2pKey), Is.EqualTo(Eip778NodeId));
        Assert.That(PeerColumnCustody.NodeIdOf(ed25519Key), Is.Null, "a discv5 node id exists only for a secp256k1 key");
    }

    [TestCase(8ul, new ulong[] { 40, 57, 61, 84, 102, 105, 113, 120 }, true, TestName = "An advertised count selects its get_custody_groups columns")]
    [TestCase(null, new ulong[] { 57, 84, 105, 113 }, false, TestName = "An unknown count falls back to the CUSTODY_REQUIREMENT groups")]
    [TestCase(Eip7594DasConstants.NumberOfCustodyGroups + 1, new ulong[] { 57, 84, 105, 113 }, false, TestName = "An out-of-range count falls back to the CUSTODY_REQUIREMENT groups")]
    public void Custody_is_the_columns_of_get_custody_groups_for_the_node_id_and_count(ulong? custodyGroupCount, ulong[] expectedColumns, bool advertised)
    {
        PeerColumnCustody custody = PeerColumnCustody.ForNode(KnownNodeId, custodyGroupCount);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(Columns(custody), Is.EqualTo(expectedColumns));
        Assert.That(custody.IsAdvertised, Is.EqualTo(advertised));
    }

    [Test]
    public void The_custody_group_count_is_read_from_a_discovered_peer_s_enr()
    {
        using PrivateKey key = new(Eip778PrivateKey);
        string enr = new BeaconNodeRecordProvider(key, IPAddress.Loopback, 9000, 9000, EnrForkId.Compute(BeaconChainSpec.Mainnet, 0), custodyGroupCount: 16).Current.ToString();

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(PeerColumnCustody.CustodyGroupCountOf(enr), Is.EqualTo(16UL));
        Assert.That(PeerColumnCustody.CustodyGroupCountOf("enr:-not-a-record"), Is.Null);
        Assert.That(PeerColumnCustody.CustodyGroupCountOf(null), Is.Null);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_connected_peer_s_custody_follows_its_metadata(CancellationToken token)
    {
        PeerBandTests.Node server = PeerBandTests.CreateNode();
        PeerBandTests.Node client = PeerBandTests.CreateNode();
        PeerBandTests.SetMatchingStatus(server, client);
        server.Metadata.Current.CustodyGroupCount = 8;
        using PrivateKey serverKey = new("1c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f29111");
        server.Store.PutMetadata(BeaconDiscovery.IdentityMetadataKey, serverKey.KeyBytes);

        await using PeerHostScope hosts = new(client.P2P, server.P2P);
        await hosts.StartAsync(token, server.P2P, client.P2P);
        Hash256 serverNodeId = serverKey.PublicKey.Hash;

        PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
        Assert.That(await peerManager.TryAddPeerAsync(PeerBandTests.LoopbackAddress(server.P2P), token), Is.True);
        PeerColumnCustody admitted = peerManager.GetBestPeers(0).Single().Custody;

        server.Metadata.Current.CustodyGroupCount = 32;
        server.Metadata.Current.SeqNumber++;
        await peerManager.RunMaintenanceRoundAsync(token);
        PeerColumnCustody refreshed = peerManager.GetBestPeers(0).Single().Custody;

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(admitted.IsAdvertised, Is.True);
        Assert.That(Columns(admitted), Is.EqualTo(Columns(PeerColumnCustody.ForNode(serverNodeId, 8))));
        Assert.That(Columns(refreshed), Is.EqualTo(Columns(PeerColumnCustody.ForNode(serverNodeId, 32))));
    }

    private static ulong[] Columns(PeerColumnCustody custody) =>
        [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c).Where(custody.Custodies)];
}
