// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Types;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;

namespace Nethermind.BeaconChain.Test.P2P;

public class QuicLoopbackTests
{
    [Test]
    [CancelAfter(45_000)]
    public async Task Quic_session_identifies_the_peer_and_exchanges_status_and_ping(CancellationToken token)
    {
        RequireQuic();
        PeerSessionNodes.Node server = PeerSessionNodes.Create(quicPort: PeerSessionNodes.FreeUdpPort());
        PeerSessionNodes.Node client = PeerSessionNodes.Create(quicPort: PeerSessionNodes.FreeUdpPort());
        await using PeerHostScope hosts = new(client.P2P, server.P2P);
        await hosts.StartAsync(token, server.P2P, client.P2P);

        ISession session = await PeerSessionNodes.DialAsync(client.P2P, server.P2P, token, quic: true);
        BeaconP2P.SessionInfo info = await client.P2P.GetSessionInfoAsync(session, token);
        StatusMessageV2 status = await client.P2P.RequestStatusAsync(session, token);
        ulong ping = await client.P2P.PingAsync(session, token);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(session.RemoteAddress.ToString(), Does.Contain("/quic-v1/"));
        Assert.That(session.RemotePeerId, Is.EqualTo(server.P2P.LocalPeerId));
        Assert.That(BeaconP2P.RemotePublicKeyOf(session)?.Type, Is.EqualTo(KeyType.Secp256K1));
        Assert.That(info.Direction, Is.EqualTo(PeerDirection.Outbound));
        Assert.That(info.AgentVersion, Is.Not.Empty);
        Assert.That(status.HeadSlot, Is.EqualTo(PeerSessionNodes.Status.HeadSlot));
        Assert.That(ping, Is.EqualTo(1ul));
    }

    [Test]
    [CancelAfter(45_000)]
    public async Task Quic_dial_rejects_a_peer_whose_certificate_has_another_peer_id(CancellationToken token)
    {
        RequireQuic();
        PeerSessionNodes.Node server = PeerSessionNodes.Create(quicPort: PeerSessionNodes.FreeUdpPort());
        PeerSessionNodes.Node client = PeerSessionNodes.Create(quicPort: PeerSessionNodes.FreeUdpPort());
        await using PeerHostScope hosts = new(client.P2P, server.P2P);
        await hosts.StartAsync(token, server.P2P, client.P2P);

        string wrong = $"{PeerSessionNodes.LoopbackAddressText(server.P2P, withPeerId: false, quic: true)}/p2p/{new Identity().PeerId}";
        Exception? failure = await Assert.CatchAsync<Exception>(async () =>
            await client.P2P.DialPeerAsync(Multiaddress.Decode(wrong), token).WaitAsync(TimeSpan.FromSeconds(15), token));

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(failure, Is.Not.Null.And.Not.InstanceOf<TimeoutException>());
        Assert.That(client.P2P.SessionCountForTest, Is.Zero);
    }

    [Test]
    [CancelAfter(45_000)]
    public async Task Disposing_an_active_quic_host_closes_its_udp_listener_and_session(CancellationToken token)
    {
        RequireQuic();
        int port = PeerSessionNodes.FreeUdpPort();
        PeerSessionNodes.Node server = PeerSessionNodes.Create(quicPort: port);
        PeerSessionNodes.Node client = PeerSessionNodes.Create(quicPort: PeerSessionNodes.FreeUdpPort());
        await using BeaconP2P clientHost = client.P2P;
        await using BeaconP2P serverHost = server.P2P;
        await serverHost.StartAsync(token);
        await clientHost.StartAsync(token);
        await PeerSessionNodes.DialAsync(clientHost, serverHost, token, quic: true);

        await serverHost.DisposeAsync();
        await PeerSessionNodes.WaitUntilAsync(() => clientHost.SessionCountForTest == 0,
            "the active QUIC session remained after its remote host was disposed", token, TimeSpan.FromSeconds(15));
        await PeerSessionNodes.WaitUntilAsync(() => CanBindUdpPort(port),
            "the QUIC listener retained its UDP port after the host was disposed", token, TimeSpan.FromSeconds(15));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Discovered_peer_falls_back_to_tcp_after_a_quic_dial_failure(CancellationToken token)
    {
        RequireQuic();
        PeerSessionNodes.Node server = PeerSessionNodes.Create(quicPort: PeerSessionNodes.FreeUdpPort());
        PeerSessionNodes.Node client = PeerSessionNodes.Create(quicPort: PeerSessionNodes.FreeUdpPort());
        await using PeerHostScope hosts = new(client.P2P, server.P2P);
        await hosts.StartAsync(token, server.P2P, client.P2P);

        string tcp = PeerSessionNodes.LoopbackAddressText(server.P2P);
        string unavailableQuic = $"/ip4/127.0.0.1/udp/{PeerSessionNodes.FreeUdpPort()}/quic-v1/p2p/{server.P2P.LocalPeerId}";
        BeaconPeerCandidate candidate = new(unavailableQuic, server.P2P.LocalPeerId!.ToString(), [], 1, "")
        {
            Addresses = [unavailableQuic, tcp],
        };
        PeerManager manager = client.CreatePeerManager();
        await manager.DialDiscoveredPeersAsync(Single(candidate), token);

        Assert.That(manager.PeerCount, Is.EqualTo(1));
        Assert.That(client.P2P.LocalPeerForTest!.Sessions.Single().RemoteAddress.ToString(), Does.Contain("/tcp/"));

        static async IAsyncEnumerable<BeaconPeerCandidate> Single(BeaconPeerCandidate value)
        {
            await Task.Yield();
            yield return value;
        }
    }

    private static void RequireQuic()
    {
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Assert.That(QuicListener.IsSupported && QuicConnection.IsSupported, Is.True, "Windows CI must exercise QUIC with the bundled MsQuic runtime.");
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported) Assert.Ignore("MsQuic is unavailable on this runner.");
    }

    private static bool CanBindUdpPort(int port)
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.AccessDenied or SocketError.AddressAlreadyInUse)
        {
            return false;
        }
    }
}
