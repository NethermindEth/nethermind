// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using static Nethermind.BeaconChain.Test.P2P.PeerSessionNodes;

namespace Nethermind.BeaconChain.Test.P2P;

[CancelAfter(60_000)]
public class IdentifyTimeoutTests
{
    private static readonly TimeSpan Within = BeaconP2P.IdentifyTimeout + TimeSpan.FromSeconds(3);

    [Test]
    public async Task A_dial_to_a_peer_that_never_answers_identify_fails_within_the_identify_bound(CancellationToken token)
    {
        UnansweredIdentifyProtocol? unanswered = null;
        await using PlainPeer peer = await PlainPeer.StartAsync(settings => unanswered = new UnansweredIdentifyProtocol(false, settings), token);
        await using BeaconP2P node = Create().P2P;
        await node.StartAsync(token);

        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<ISession> dial = node.DialPeerAsync(peer.Address, attempt.Token);
        await unanswered!.Requested.Task.WaitAsync(token);
        // The library removes a closed session under this lock, so holding it keeps the failed session listed while the dial fails:
        // the window in which the dial could hand that session back. The dial's own lookup takes the lock too, so it waits here.
        Task heldListing = Task.Run(() =>
        {
            lock (node.LocalPeerForTest!.Sessions)
            {
                ((IAsyncResult)dial).AsyncWaitHandle.WaitOne(BeaconP2P.IdentifyTimeout + TimeSpan.FromSeconds(1));
            }
        }, token);
        try
        {
            Assert.That(await Task.WhenAny(dial, Task.Delay(Within, token)), Is.SameAs(dial), "the dial ended within the identify bound");
        }
        finally
        {
            await heldListing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            attempt.Cancel();
            await ((Task)dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        Assert.That(dial.Status, Is.EqualTo(TaskStatus.Faulted), $"no unidentified session is handed back ({dial.Exception?.GetBaseException().Message})");
        Exception failure = dial.Exception!.InnerException!;
        Assert.That(PeerManager.DescribeFailure(failure), Is.Not.EqualTo(new AggregateException().Message), $"the dial failed with {failure.GetType().Name}: {failure.Message}");
        Assert.That(failure.Message, Does.Not.Contain("closed before it was established"), "an identify timeout is not reported as a closed session");
        await WaitUntilAsync(() => node.SessionCountForTest == 0, "the unidentified session was left open", token, Within);
        Assert.That(node.IdentifyTimeoutsForTest, Is.EqualTo(1), "the stall is counted as an identify timeout");
    }

    [Test]
    public async Task A_session_from_a_peer_that_never_answers_identify_is_closed_within_the_identify_bound(CancellationToken token)
    {
        await using BeaconP2P node = Create().P2P;
        await node.StartAsync(token);
        int established = 0;
        node.SessionEstablished += (_, _) => Interlocked.Increment(ref established);
        UnansweredIdentifyProtocol? unanswered = null;
        await using PlainPeer peer = await PlainPeer.StartAsync(settings => unanswered = new UnansweredIdentifyProtocol(false, settings), token, pingOnDial: true);
        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<ISession> dial = peer.Peer.DialAsync(LoopbackAddress(node), attempt.Token);
        try
        {
            await unanswered!.Requested.Task.WaitAsync(token);
            await WaitUntilAsync(() => node.SessionCountForTest == 0, "the unidentified session was left open", token, Within);
        }
        finally
        {
            attempt.Cancel();
            await ((Task)dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        Assert.That(Volatile.Read(ref established), Is.Zero, "an unidentified session is never reported as established");
        Assert.That(node.IdentifyTimeoutsForTest, Is.EqualTo(1), "an accepted identify protocol that never answers reaches the identify deadline");
    }

    [Test]
    public async Task Unsupported_identify_is_rejected_without_waiting_for_the_identify_deadline([Values] bool inbound, CancellationToken token)
    {
        await using BeaconP2P node = Create().P2P;
        await node.StartAsync(token);
        int established = 0;
        node.SessionEstablished += (_, _) => Interlocked.Increment(ref established);
        await using PlainPeer peer = await PlainPeer.StartAsync(settings => new UnansweredIdentifyProtocol(true, settings), token, pingOnDial: inbound);
        using BeaconP2P.SessionWatch watch = node.WatchSessions(peer.Peer.Identity.PeerId);
        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<ISession> dial = inbound ? peer.Peer.DialAsync(LoopbackAddress(node), attempt.Token) : node.DialPeerAsync(peer.Address, attempt.Token);
        TimeSpan prompt = TimeSpan.FromSeconds(2);
        try
        {
            await WaitUntilAsync(() => watch.Opened != 0, "fixture: no session reached the node", token);
            if (!inbound)
            {
                Assert.That(await Task.WhenAny(dial, Task.Delay(prompt, token)), Is.SameAs(dial), "an unsupported identify protocol fails before the identify deadline");
                Assert.That(dial.Status, Is.EqualTo(TaskStatus.Faulted));
                Exception failure = dial.Exception!.InnerException!;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(PeerFailureClassifier.Classify(failure), Is.AnyOf(PeerFailureReason.RequestFailed, PeerFailureReason.SessionClosed),
                        "identify rejection can surface as the raw closed channel or a wrapped failed connection");
                    Assert.That(failure.GetBaseException(), Is.Not.InstanceOf<TimeoutException>());
                    Assert.That(PeerManager.DescribeFailure(failure), Does.Not.Contain("timed out"));
                }
            }
            await WaitUntilAsync(() => node.SessionCountForTest == 0, "the rejected unidentified session stayed listed", token, prompt);
            Assert.That(await Task.WhenAny(dial, Task.Delay(prompt, token)), Is.SameAs(dial), "the remote handshake also ended");
            await ((Task)dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (dial.IsCompletedSuccessfully)
            {
                ISession remote = await dial;
                await WaitUntilAsync(() => remote.Closed.IsCancellationRequested, "the remote session remained open after identify rejection", token, prompt);
            }
        }
        finally
        {
            attempt.Cancel();
            await ((Task)dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(watch.Opened, Is.EqualTo(1));
        Assert.That(Volatile.Read(ref established), Is.Zero, "an unidentified session is never reported as established");
        Assert.That(node.IdentifyTimeoutsForTest, Is.Zero, "protocol rejection is not an identify timeout");
    }

    [Test]
    public async Task A_session_closed_during_identify_is_not_an_identify_timeout([Values] bool localClose, CancellationToken token)
    {
        UnansweredIdentifyProtocol? unanswered = null;
        await using PlainPeer peer = await PlainPeer.StartAsync(
            settings => localClose ? unanswered = new UnansweredIdentifyProtocol(false, settings) : new ClosingIdentifyProtocol(settings), token);
        await using BeaconP2P node = Create().P2P;
        await node.StartAsync(token);

        Task<ISession> dial = node.DialPeerAsync(peer.Address, token);
        if (localClose)
        {
            await unanswered!.Requested.Task.WaitAsync(token);
            LocalPeer.Session session;
            lock (node.LocalPeerForTest!.Sessions)
            {
                session = node.LocalPeerForTest.Sessions[0];
            }

            await session.DisconnectAsync();
        }

        Assert.That(await Task.WhenAny(dial, Task.Delay(Within, token)), Is.SameAs(dial), "the dial ended");
        Assert.That(dial.Status, Is.EqualTo(TaskStatus.Faulted), "fixture: the dial failed");
        Exception failure = dial.Exception!.InnerException!;
        string description = $"the dial failed with {failure.GetType().Name}: {failure.Message}";
        Assert.That(PeerManager.DescribeFailure(failure), Is.Not.EqualTo("request timed out").And.Not.EqualTo(new AggregateException().Message), description);
        Assert.That(PeerFailureClassifier.Classify(failure), Is.EqualTo(PeerFailureReason.RequestFailed), description);
        await WaitUntilAsync(() => node.SessionCountForTest == 0, "fixture: the closed session stayed listed", token, Within);
        await Task.Delay(BeaconP2P.IdentifyTimeout, token);
        Assert.That(node.IdentifyTimeoutsForTest, Is.Zero, "a closed session is not counted as an identify timeout");
    }

    private sealed class ClosingIdentifyProtocol(IProtocolStackSettings settings) : IdentifyProtocol(settings), ISessionListenerProtocol, IProtocol
    {
        public new Task ListenAsync(IChannel downChannel, ISessionContext context) => context.DisconnectAsync();
    }

    private sealed class UnansweredIdentifyProtocol(bool unsupported, IProtocolStackSettings settings) : IdentifyProtocol(settings), ISessionListenerProtocol, IProtocol
    {
        internal TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public new string Id => unsupported ? "/test/not-identify/1.0.0" : base.Id;
        public new async Task ListenAsync(IChannel downChannel, ISessionContext context)
        {
            Requested.TrySetResult();
            await downChannel;
        }
    }
}
