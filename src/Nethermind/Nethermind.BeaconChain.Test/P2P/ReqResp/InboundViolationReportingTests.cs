// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.Libp2p.Core;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.ReqResp;

/// <summary>A peer that breaks the protocol on a stream it opened is reported whatever selection thinks of it, since selection leaves out the peers most likely to misbehave.</summary>
public class InboundViolationReportingTests
{
    // PeerManager's failure limit.
    private const int Limit = 8;

    [Test]
    [CancelAfter(60_000)]
    public async Task A_violation_is_recorded_against_a_connected_peer_that_selection_leaves_out(CancellationToken token)
    {
        PeerManager? manager = null;
        PeerSessionNodes.Node listener = PeerSessionNodes.Create(peerPool: new Lazy<IBeaconSyncPeerPool>(() => manager!));
        PeerSessionNodes.Node remote = PeerSessionNodes.Create();
        await using BeaconP2P listenerHost = listener.P2P;
        await using BeaconP2P remoteHost = remote.P2P;
        await listenerHost.StartAsync(token);
        await remoteHost.StartAsync(token);
        manager = listener.CreatePeerManager();
        Assert.That(await manager.TryAddPeerAsync(PeerSessionNodes.LoopbackAddressText(remoteHost), token), Is.True);
        IBeaconSyncPeer peer = manager.GetBestPeers(0).Single();
        // Invalid data keeps a peer at the limit out even when no other peer is offered.
        for (int i = 0; i < Limit / 2; i++)
        {
            peer.ReportFailure(PeerFailureReason.ProtocolViolation);
        }

        Assert.That(manager.GetBestPeers(0), Is.Empty, "test setup: selection leaves the peer out");
        long before = PeerManager.FailuresReportedForTest(peer);

        listenerHost.ReportRequestViolation(remoteHost.LocalPeerId ?? throw new InvalidOperationException("not started"), "bytes after the request");

        Assert.That(PeerManager.FailuresReportedForTest(peer), Is.EqualTo(before + 1));
    }

    // Attributing a violation to any connected peer would penalise an honest one for a stranger's stream.
    [Test]
    [CancelAfter(60_000)]
    public async Task A_violation_by_a_peer_that_is_not_connected_is_not_recorded_against_a_connected_one(CancellationToken token)
    {
        PeerManager? manager = null;
        PeerSessionNodes.Node listener = PeerSessionNodes.Create(peerPool: new Lazy<IBeaconSyncPeerPool>(() => manager!));
        PeerSessionNodes.Node remote = PeerSessionNodes.Create();
        PeerSessionNodes.Node stranger = PeerSessionNodes.Create();
        await using BeaconP2P listenerHost = listener.P2P;
        await using BeaconP2P remoteHost = remote.P2P;
        await using BeaconP2P strangerHost = stranger.P2P;
        await listenerHost.StartAsync(token);
        await remoteHost.StartAsync(token);
        await strangerHost.StartAsync(token);
        manager = listener.CreatePeerManager();
        Assert.That(await manager.TryAddPeerAsync(PeerSessionNodes.LoopbackAddressText(remoteHost), token), Is.True);
        IBeaconSyncPeer connected = manager.GetBestPeers(0).Single();
        long before = PeerManager.FailuresReportedForTest(connected);
        PeerId strangerId = strangerHost.LocalPeerId ?? throw new InvalidOperationException("not started");

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(manager.TryReportInboundViolation(strangerId, "bytes after the request"), Is.False);
        Assert.DoesNotThrow(() => listenerHost.ReportRequestViolation(strangerId, "bytes after the request"));
        Assert.That(PeerManager.FailuresReportedForTest(connected), Is.EqualTo(before));
    }
}
