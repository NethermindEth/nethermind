// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Logging;
using NSubstitute;
using static Nethermind.BeaconChain.Test.P2P.PeerBandTests;

namespace Nethermind.BeaconChain.Test.P2P;

[CancelAfter(60_000)]
public class PeerAdmissionMetadataTests
{
    // The metadata timeout plus margin for a loaded loopback dial, still under the 15 s request timeout a missing bound would cost.
    private static readonly TimeSpan Within = PeerManager.AdmissionMetadataTimeout + TimeSpan.FromSeconds(5);

    [Test]
    public async Task An_admitted_peer_is_announced_once_it_is_in_the_pool(CancellationToken token)
    {
        Node client = CreateNode();
        Node other = CreateNode();
        SetMatchingStatus(client, other);

        await using PeerHostScope hosts = new(client, other);
        await hosts.StartAsync(token, client.P2P, other.P2P);
        PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
        List<(IBeaconSyncPeer Peer, bool InPool, bool CustodyKnown)> announced = [];
        peerManager.PeerAdmitted += peer => announced.Add((peer, peerManager.GetBestPeers(0).Contains(peer), peer.Custody.IsAdvertised));

        bool admitted = await peerManager.TryAddPeerAsync(LoopbackAddress(other.P2P), token);

        Assert.That(admitted, Is.True);
        Assert.That(announced, Has.Count.EqualTo(1));
        string statusProtocol = new StatusProtocolV2(other.StatusHolder).Id;
        IBeaconSyncPeer peer = announced[0].Peer;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(announced[0].InPool, Is.True);
            Assert.That(announced[0].CustodyKnown, Is.True, "the peer's MetaData was read before the announcement");
            Assert.That(peer.AdvertisedProtocols, Does.Contain(statusProtocol));
        }

        client.P2P.PeerInfoForTest(other.P2P.LocalPeerId!).SupportedProtocols = [];
        Assert.That(peer.AdvertisedProtocols, Is.Empty);
        Assert.That(client.P2P.TryGetEstablishedSession(other.P2P.LocalPeerId!, out ISession? session), Is.True);
        string wanted = new LightClientBootstrapProtocol(BeaconChainSpec.Mainnet).Id;
        string[] saturated = [.. Enumerable.Range(0, BeaconP2P.MaxIdentifyProtocols).Select(static i => $"/other/{i}")];
        client.P2P.RecordIdentify(session!, new IdentifyResult
        {
            PeerId = other.P2P.LocalPeerId!,
            Protocols = [.. saturated, wanted],
        });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(client.P2P.PeerInfoForTest(other.P2P.LocalPeerId!).SupportedProtocols, Is.EqualTo(saturated));
            Assert.That(peer.AdvertisedProtocols, Is.Null, "a truncated list cannot establish that bootstrap is unsupported");
        }
        client.P2P.PeerInfoForTest(other.P2P.LocalPeerId!).SupportedProtocols = saturated;
        Assert.That(peer.AdvertisedProtocols, Is.Null, "a saturated pushed list is also incomplete");
        client.P2P.PeerInfoForTest(other.P2P.LocalPeerId!).SupportedProtocols = null;
        Assert.That(peer.AdvertisedProtocols, Is.Null);
        other.P2P.LocalPeerForTest!.ListenAddresses.Add(Multiaddress.Decode($"/ip4/127.0.0.1/tcp/1/p2p/{other.P2P.LocalPeerId}"));
        await PeerSessionNodes.WaitUntilAsync(() => peer.AdvertisedProtocols?.Contains(statusProtocol) == true,
            "the admitted peer did not observe the updated identify push", token);

        await session!.DisconnectAsync();
        Assert.That(peer.AdvertisedProtocols, Is.Null, "closed sessions cannot borrow later peer-store capabilities");
    }

    [Test]
    public async Task A_peer_without_a_session_identify_answer_does_not_use_old_peer_store_protocols()
    {
        Node client = CreateNode();
        await using PeerHostScope hosts = new(client);
        PeerId peerId = new Identity(privateKey: null, KeyType.Secp256K1).PeerId;
        client.P2P.PeerInfoForTest(peerId).SupportedProtocols = [new StatusProtocolV2(client.StatusHolder).Id];
        ISession session = Substitute.For<ISession>();
        session.RemotePeerId.Returns(peerId);
        PeerManager manager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
        IBeaconSyncPeer peer = manager.AddPeerForTest(session, $"/ip4/127.0.0.1/tcp/1/p2p/{peerId}", removeWhenSessionCloses: false);

        Assert.That(peer.AdvertisedProtocols, Is.Null);
    }

    [Test]
    public async Task A_peer_that_never_answers_metadata_is_admitted_within_the_admission_timeout_and_frees_the_dial_slot(CancellationToken token)
    {
        Node client = CreateNode();
        Node other = CreateNode();
        SetMatchingStatus(client, other);
        client.Config.MaxConcurrentOutboundDials = 1;
        // A band of exactly two: the pending admission must count once, or the second peer is refused at the ceiling.
        client.Config.TargetPeerCount = 2;
        client.Config.MaxPeerCount = 2;
        StallingMetadataProtocol metadata = new();
        await using PlainPeer silent = await PlainPeer.StartAsync(static settings => new Nethermind.Libp2p.Protocols.IdentifyProtocol(settings), token,
            new ScriptedStatusSource(_ => client.StatusHolder.CurrentStatus), additionalProtocol: metadata);

        await using PeerHostScope hosts = new(client, other);
        await hosts.StartAsync(token, client.P2P, other.P2P);
        PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
        string silentAddress = silent.Address.ToString();
        if (!silentAddress.Contains("/p2p/", StringComparison.Ordinal))
        {
            silentAddress += $"/p2p/{silent.Peer.Identity.PeerId}";
        }

        Task<bool> silentAdmission = peerManager.TryAddPeerAsync(silentAddress, token);
        await metadata.Requested.Task.WaitAsync(token);
        bool usableWhileMetadataPends = !silentAdmission.IsCompleted && peerManager.GetBestPeers(0).Count == 1;
        Task<bool> otherAdmission = peerManager.TryAddPeerAsync(LoopbackAddress(other.P2P), token);

        Task both = Task.WhenAll(silentAdmission, otherAdmission);
        bool inTime = await Task.WhenAny(both, Task.Delay(Within, token)) == both;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(usableWhileMetadataPends, Is.True, "the peer is in the pool while its metadata is awaited");
        Assert.That(inTime, Is.True, "the admission ends within the metadata timeout, so the next dial gets the slot");
        Assert.That(inTime && silentAdmission.Result, Is.True);
        Assert.That(inTime && otherAdmission.Result, Is.True);
        Assert.That(peerManager.PeerCount, Is.EqualTo(2));
    }

    [Test]
    public async Task A_peer_that_breaks_the_protocol_and_closes_while_its_metadata_is_awaited_keeps_its_dial_backoff(CancellationToken token)
    {
        Node client = CreateNode();
        await using BeaconDiscovery discovery = new(client.Config, BeaconChainSpec.Mainnet, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()),
            new RangeSyncTests.FixedIPResolver(IPAddress.Loopback), new ManualTimestamper(), LimboLogs.Instance);
        StallingMetadataProtocol metadata = new();
        await using PlainPeer silent = await PlainPeer.StartAsync(static settings => new Nethermind.Libp2p.Protocols.IdentifyProtocol(settings), token,
            new ScriptedStatusSource(_ => client.StatusHolder.CurrentStatus), additionalProtocol: metadata);

        await using PeerHostScope hosts = new(client);
        await client.P2P.StartAsync(token);
        PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance, discovery);
        string silentAddress = silent.Address.ToString();
        if (!silentAddress.Contains("/p2p/", StringComparison.Ordinal))
        {
            silentAddress += $"/p2p/{silent.Peer.Identity.PeerId}";
        }

        Task<bool> admission = peerManager.TryAddPeerAsync(silentAddress, token);
        await metadata.Requested.Task.WaitAsync(token);
        Assert.That(admission.IsCompleted, Is.False, "fixture: the metadata is still awaited");
        peerManager.GetBestPeers(0).Single().ReportFailure(PeerFailureReason.ProtocolViolation, "a block failed its parent-root check");
        Assert.That(client.P2P.TryGetEstablishedSession(silent.Peer.Identity.PeerId, out ISession? session), Is.True);
        await session!.DisconnectAsync();

        bool admitted = await admission.WaitAsync(Within, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(admitted, Is.False, "the session closed before the admission ended");
        Assert.That(discovery.DialHistory.Quality(silentAddress), Is.EqualTo(-1), "the address stays backed off, one step for one failed dial");
    }
}
