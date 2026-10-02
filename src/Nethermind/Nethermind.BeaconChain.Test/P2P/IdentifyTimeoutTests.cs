// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.PeerSessionNodes;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// A peer that never completes identify must not keep a session: every session waits on identify before it can be admitted,
/// so one left open would sit outside the peer band and the health checks for as long as the peer likes.
/// </summary>
public class IdentifyTimeoutTests
{
    // The identify bound plus a loopback teardown.
    private static readonly TimeSpan Within = IdentifyAgentVersionProbe.ReadTimeout + TimeSpan.FromSeconds(3);

    public enum Stall
    {
        /// <summary>The peer does not list identify: it answers <c>na</c>, and the library's dial then waits out its bound rather than failing.</summary>
        Negotiation,

        /// <summary>The peer accepts the identify stream and never writes its answer.</summary>
        Answer,
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_dial_to_a_peer_that_never_answers_identify_fails_within_the_identify_bound([Values] Stall stall, CancellationToken token)
    {
        await using PlainPeer peer = await PlainPeer.StartAsync(settings => new StallingIdentifyProtocol(stall, settings), token);
        await using BeaconP2P node = Create().P2P;
        await node.StartAsync(token);

        Task<ISession> dial = node.DialPeerAsync(peer.Address, token);
        await WaitUntilAsync(() => node.SessionCountForTest == 1, "fixture: the dial never opened a session", token);
        // The library removes a closed session under this lock, so holding it keeps the failed session listed while the dial fails:
        // the window in which the dial could hand that session back. The dial's own lookup takes the lock too, so it waits here.
        Task heldListing = Task.Run(() =>
        {
            lock (node.LocalPeerForTest!.Sessions)
            {
                ((IAsyncResult)dial).AsyncWaitHandle.WaitOne(IdentifyAgentVersionProbe.ReadTimeout + TimeSpan.FromSeconds(1));
            }
        }, token);
        Assert.That(await Task.WhenAny(dial, Task.Delay(Within, token)), Is.SameAs(dial), "the dial ended within the identify bound");
        await heldListing;
        Assert.That(dial.Status, Is.EqualTo(TaskStatus.Faulted), $"no unidentified session is handed back ({dial.Exception?.GetBaseException().Message})");
        // The failure PeerManager logs for the dial names the timeout rather than an empty aggregate.
        Exception failure = dial.Exception!.InnerException!;
        Assert.That(PeerManager.DescribeFailure(failure), Is.EqualTo("request timed out"), $"the dial failed with {failure.GetType().Name}: {failure.Message}");
        await WaitUntilAsync(() => node.SessionCountForTest == 0, "the unidentified session was left open", token, Within);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_session_from_a_peer_that_never_answers_identify_is_closed_within_the_identify_bound([Values] Stall stall, CancellationToken token)
    {
        await using BeaconP2P node = Create().P2P;
        await node.StartAsync(token);
        int established = 0;
        node.SessionEstablished += (_, _) => Interlocked.Increment(ref established);
        await using PlainPeer peer = await PlainPeer.StartAsync(settings => new StallingIdentifyProtocol(stall, settings), token);

        ISession session = await peer.Peer.DialAsync(LoopbackAddress(node), token).WaitAsync(token);
        // The node completes its side of the upgrade only once the dialer sends on the connection.
        _ = session.DialAsync<PingProtocol>(token);
        await WaitUntilAsync(() => node.SessionCountForTest == 1, "fixture: the peer's session never reached the node", token);
        await WaitUntilAsync(() => node.SessionCountForTest == 0, "the unidentified session was left open", token, Within);
        Assert.That(Volatile.Read(ref established), Is.Zero, "an unidentified session is never reported as established");
    }

    private sealed class StallingIdentifyProtocol(Stall stall, IProtocolStackSettings settings) : IdentifyProtocol(settings), ISessionListenerProtocol, IProtocol
    {
        public new string Id => stall == Stall.Negotiation ? "/test/not-identify/1.0.0" : base.Id;

        // Returns only once the stream or its session closes.
        public new async Task ListenAsync(IChannel downChannel, ISessionContext context) => await downChannel.ReadAsync(1);
    }
}
