// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.PeerSessionNodes;
using KeyType = Nethermind.Libp2p.Core.Dto.KeyType;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// A peer whose libp2p session closed can never answer again: it leaves the pool at once, without a failure counted against it,
/// so selection and the failure budget never see it and the dial loop can replace it.
/// </summary>
public class PeerSessionCloseTests
{
    // A real peer id, so an inbound violation can name it.
    private static readonly Identity PeerIdentity = new(privateKey: null, KeyType.Secp256K1);
    private static readonly string PeerAddress = $"/ip4/10.0.0.1/tcp/9000/p2p/{PeerIdentity.PeerId}";

    // Well under the admission wait's 5 s poll, so only the wake on removal can end the wait in time.
    private static readonly TimeSpan ReplacementBound = TimeSpan.FromSeconds(2);

    [Test]
    [CancelAfter(30_000)]
    public async Task A_peer_whose_session_closes_leaves_the_pool_at_once_without_a_failure_and_a_replacement_dial_is_let_through(CancellationToken token)
    {
        Node node = Create();
        node.Config.TargetPeerCount = 1;
        node.Config.FaultDisconnectsBeforeBan = 1;
        await using PeerHostScope hosts = new(node.P2P);
        await node.P2P.StartAsync(token);
        PeerManager manager = node.CreatePeerManager();
        LocalPeer.Session session = RequestFailureCauseTests.AddWedgedSession(node.P2P);
        IBeaconSyncPeer peer = manager.AddPeerForTest(session, PeerAddress, Status);
        Task<IReadOnlyList<DataColumnSidecar>>[] requests = [.. Enumerable.Range(0, 3).Select(i => peer.RequestDataColumnSidecarsByRootAsync(RequestFailureCauseTests.Identifiers(i), token))];
        Task replacement = manager.WaitForAdmissionCapacityAsync(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PeerManager.RequestsInFlightForTest(peer), Is.EqualTo(3), "fixture: two requests hold the slots and one waits for a slot");
            Assert.That(manager.GetBestPeers(0), Is.EqualTo(new[] { peer }), "fixture: the peer is selectable");
            Assert.That(replacement.IsCompleted, Is.False, "fixture: the pool is at its target, so no dial is let through");
        }

        await session.DisconnectAsync();

        Assert.DoesNotThrowAsync(() => replacement.WaitAsync(ReplacementBound, token), "the dial loop is let through as soon as the peer leaves, not at its next poll");
        IOException? queued = Assert.ThrowsAsync<IOException>(async () => await requests[2]);
        // What sync and the health check do with such a failure.
        peer.ReportFailure(PeerFailureClassifier.Classify(queued!), queued!.Message);
        await manager.HandleHealthFailureAsync(peer, queued, startedAt: 0, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued.Message, Does.Contain("waiting for a request slot: its libp2p session closed"), "the queued request ends as a disconnect");
            Assert.That(manager.PeerCount, Is.Zero, "the peer left the pool");
            Assert.That(manager.GetBestPeers(0), Is.Empty, "selection never offers it");
            Assert.That(PeerManager.FailuresReportedForTest(peer), Is.Zero, "a request failure of a closed session is not counted");
            Assert.That(PeerManager.ConsecutiveFailuresForTest(peer), Is.Zero, "neither as a request failure nor as a failed health check");
            Assert.That(manager.IsBannedForTest(PeerManager.ExtractPeerIdForTest(PeerAddress)), Is.False, "a lost session is no step toward a ban");
        }

        Assert.CatchAsync<IOException>(() => Task.WhenAll(requests), "the requests that held the slots end as disconnects too");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_closed_session_puts_no_dial_backoff_on_the_peers_address(CancellationToken token)
    {
        Node node = Create();
        await using BeaconDiscovery discovery = new(node.Config, BeaconChainSpec.Mainnet, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()),
            new RangeSyncTests.FixedIPResolver(IPAddress.Loopback), new ManualTimestamper(), LimboLogs.Instance);
        await using PeerHostScope hosts = new(node.P2P);
        await node.P2P.StartAsync(token);
        PeerManager manager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance, discovery);
        LocalPeer.Session session = RequestFailureCauseTests.AddWedgedSession(node.P2P);
        manager.AddPeerForTest(session, PeerAddress, Status);

        await session.DisconnectAsync();

        // The removal writes the disconnect record after any dial outcome.
        await WaitUntilAsync(() => manager.GetPeerDiagnostics().Any(static peer => peer.DisconnectCount == 1), "the peer's removal never finished", token, ReplacementBound);
        Assert.That(discovery.DialHistory.Quality(PeerAddress), Is.Zero, "the address is dialed again at once, as for a peer never dialed");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_peer_whose_session_closed_before_it_was_recorded_never_stays_in_the_pool(CancellationToken token)
    {
        Node node = Create();
        await using PeerHostScope hosts = new(node.P2P);
        await node.P2P.StartAsync(token);
        PeerManager manager = node.CreatePeerManager();
        LocalPeer.Session session = RequestFailureCauseTests.AddWedgedSession(node.P2P);
        await session.DisconnectAsync();
        ulong dropped = Metrics.BeaconChainPeersDropped;
        long droppedAsClosed = DroppedAsSessionClosed();

        manager.AddPeerForTest(session, PeerAddress, Status);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(manager.PeerCount, Is.Zero);
        Assert.That(Metrics.BeaconChainPeersDropped, Is.EqualTo(dropped + 1));
        Assert.That(DroppedAsSessionClosed(), Is.EqualTo(droppedAsClosed + 1), "the per-reason series sum to the dropped total");
    }

    public enum Violation
    {
        RequestBeforeClose,
        HealthCheckBeforeClose,
        RequestAfterRemoval,
        HealthCheckAfterRemoval,
        InboundRequestAfterRemoval,
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_that_breaks_the_protocol_and_closes_its_session_is_banned_after_the_configured_fault_disconnects([Values] Violation violation, CancellationToken token)
    {
        Node node = Create();
        node.Config.FaultDisconnectsBeforeBan = 2;
        await using BeaconDiscovery discovery = new(node.Config, BeaconChainSpec.Mainnet, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()),
            new RangeSyncTests.FixedIPResolver(IPAddress.Loopback), new ManualTimestamper(), LimboLogs.Instance);
        await using PeerHostScope hosts = new(node.P2P);
        await node.P2P.StartAsync(token);
        PeerManager manager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance, discovery);
        for (int round = 1; round <= node.Config.FaultDisconnectsBeforeBan; round++)
        {
            Assert.That(manager.IsBannedForTest(PeerId), Is.False, "fixture: no ban before the last round");
            await ViolateAndCloseAsync(manager, node.P2P, violation, round, token);
        }

        // The ban is set just after the disconnect is counted, which is what the rounds wait for.
        await WaitUntilAsync(() => manager.IsBannedForTest(PeerId), "closing the session after a bad reply escaped the ban", token, ReplacementBound);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(discovery.DialHistory.Quality(PeerAddress), Is.LessThan(0), "its address is not dialed again at once");
        Assert.That(manager.GetPeerDiagnostics().Single().DisconnectCount, Is.EqualTo(node.Config.FaultDisconnectsBeforeBan), "one disconnect per session");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_drop_that_began_before_the_session_closed_does_not_record_the_peer_again(CancellationToken token)
    {
        Node node = Create();
        node.Config.FaultDisconnectsBeforeBan = 2;
        await using PeerHostScope hosts = new(node.P2P);
        await node.P2P.StartAsync(token);
        PeerManager manager = node.CreatePeerManager();
        IBeaconSyncPeer closed = await ViolateAndCloseAsync(manager, node.P2P, Violation.RequestBeforeClose, 1, token);
        ulong dropped = Metrics.BeaconChainPeersDropped;

        await manager.DropForTest(closed, GoodbyeReason.TooManyPeers, "over the configured peer band");

        PeerManager.PeerDiagnostics record = manager.GetPeerDiagnostics().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.DisconnectCount, Is.EqualTo(1), "one session, one disconnect");
            Assert.That(record.LastDisconnectReason, Is.EqualTo("Fault"));
            Assert.That(Metrics.BeaconChainPeersDropped, Is.EqualTo(dropped), "the close already counted the drop");
        }

        await ViolateAndCloseAsync(manager, node.P2P, Violation.RequestBeforeClose, 2, token);
        await WaitUntilAsync(() => manager.IsBannedForTest(PeerId), "the stale drop reset the fault streak", token, ReplacementBound);
    }

    /// <summary>Admits a peer on a new session, has it break the protocol as <paramref name="violation"/> says, and closes the session.</summary>
    /// <param name="round">The disconnects the peer id has once this returns.</param>
    private static async Task<IBeaconSyncPeer> ViolateAndCloseAsync(PeerManager manager, BeaconP2P p2p, Violation violation, int round, CancellationToken token)
    {
        LocalPeer.Session session = RequestFailureCauseTests.AddWedgedSession(p2p);
        IBeaconSyncPeer peer = manager.AddPeerForTest(session, PeerAddress, Status);
        bool afterRemoval = violation is Violation.RequestAfterRemoval or Violation.HealthCheckAfterRemoval or Violation.InboundRequestAfterRemoval;
        if (!afterRemoval) await ViolateAsync();
        await session.DisconnectAsync();
        await WaitUntilAsync(() => manager.GetPeerDiagnostics().Any(p => p.DisconnectCount == round), "the peer's removal never finished", token, ReplacementBound);
        if (afterRemoval) await ViolateAsync();
        return peer;

        Task ViolateAsync()
        {
            if (violation is Violation.HealthCheckBeforeClose or Violation.HealthCheckAfterRemoval)
            {
                return manager.HandleHealthFailureAsync(peer, new InvalidDataException("the status reply failed its content check"), startedAt: 0, token);
            }

            if (violation is Violation.InboundRequestAfterRemoval)
            {
                Assert.That(manager.TryReportInboundViolation(PeerIdentity.PeerId, "bytes after the request"), Is.True, "the closed peer still takes the violation");
                return Task.CompletedTask;
            }

            peer.ReportFailure(PeerFailureReason.ProtocolViolation, "a block failed its parent-root check");
            return Task.CompletedTask;
        }
    }

    private static string PeerId => PeerManager.ExtractPeerIdForTest(PeerAddress);

    private static long DroppedAsSessionClosed() => Metrics.BeaconChainPeersDroppedByReason.GetValueOrDefault(new StringLabel("SessionClosed"));

    [Test]
    [CancelAfter(30_000)]
    public async Task Dropping_a_peer_whose_address_a_newer_peer_took_leaves_the_newer_one_in_the_pool(CancellationToken token)
    {
        Node node = Create();
        await using PeerHostScope hosts = new(node.P2P);
        PeerManager manager = node.CreatePeerManager();
        IBeaconSyncPeer older = manager.AddPeerForTest(Substitute.For<ISession>(), PeerAddress, Status);
        IBeaconSyncPeer newer = manager.AddPeerForTest(Substitute.For<ISession>(), PeerAddress, Status);

        for (int i = 0; i < 8; i++)
        {
            await manager.HandleHealthFailureAsync(older, new InvalidDataException("bad reply"), startedAt: 0, token);
        }

        Assert.That(manager.GetBestPeers(0), Is.EqualTo(new[] { newer }), "a drop that started before the address changed hands removes only the peer it names");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_churn_of_identities_keeps_only_the_newest_closed_peers_up_to_the_capacity(CancellationToken token)
    {
        Node node = Create();
        node.Config.MaxPeerCount = 2;
        await using PeerHostScope hosts = new(node.P2P);
        await node.P2P.StartAsync(token);
        PeerManager manager = node.CreatePeerManager();
        int churn = manager.ClosedPeerCapacity + 2;
        Identity[] identities = [.. Enumerable.Range(0, churn).Select(static _ => new Identity(privateKey: null, KeyType.Secp256K1))];
        foreach (Identity identity in identities)
        {
            await CloseAsync(identity, disconnects: 1);
        }

        // The oldest kept id closes again, so its first entry is the next to go and must not take the new one with it.
        Identity again = identities[churn - manager.ClosedPeerCapacity];
        await CloseAsync(again, disconnects: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.ClosedPeerCountForTest, Is.EqualTo(manager.ClosedPeerCapacity));
            Assert.That(identities.Select(identity => manager.TryReportInboundViolation(identity.PeerId, "bytes after the request")),
                Is.EqualTo(Enumerable.Range(0, churn).Select(i => i >= churn - manager.ClosedPeerCapacity)), "the newest are kept, the oldest forgotten");
        }

        async Task CloseAsync(Identity identity, int disconnects)
        {
            LocalPeer.Session session = RequestFailureCauseTests.AddWedgedSession(node.P2P);
            manager.AddPeerForTest(session, $"/ip4/10.0.0.1/tcp/9000/p2p/{identity.PeerId}", Status);
            await session.DisconnectAsync();
            string peerId = identity.PeerId.ToString();
            await WaitUntilAsync(() => manager.GetPeerDiagnostics().Any(p => p.PeerId == peerId && p.DisconnectCount == disconnects), "the peer's removal never finished", token, ReplacementBound);
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public Task A_dialed_peer_that_breaks_the_protocol_and_closes_after_its_admission_backs_its_address_off(CancellationToken token) =>
        RetryStalledAsync(ViolationAfterDialAsync, token, TimeSpan.FromSeconds(20));

    private static async Task ViolationAfterDialAsync(CancellationToken token)
    {
        Node remote = Create();
        Node local = Create();
        await using BeaconDiscovery discovery = new(local.Config, BeaconChainSpec.Mainnet, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()),
            new RangeSyncTests.FixedIPResolver(IPAddress.Loopback), new ManualTimestamper(), LimboLogs.Instance);
        await using PeerHostScope hosts = new(local.P2P, remote.P2P);
        await hosts.StartAsync(token, remote.P2P, local.P2P);
        PeerManager manager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance, discovery);
        string address = LoopbackAddressText(remote.P2P);
        if (!await manager.TryAddPeerAsync(address, token))
        {
            ThrowIfIdentifyStalled(local.P2P, remote.P2P);
            throw new TimeoutException("the dial was not admitted");
        }

        manager.GetBestPeers(0).Single().ReportFailure(PeerFailureReason.ProtocolViolation, "a block failed its parent-root check");
        Assert.That(local.P2P.TryGetEstablishedSession(remote.P2P.LocalPeerId!, out ISession? session), Is.True);
        await session!.DisconnectAsync();
        await WaitUntilAsync(() => manager.GetPeerDiagnostics().Any(static peer => peer.DisconnectCount == 1), "the peer's removal never finished", token, ReplacementBound);

        Assert.That(discovery.DialHistory.Quality(address), Is.EqualTo(-1), "the fault close turns the dial's recorded success into one failure");
    }

    [Test]
    [CancelAfter(60_000)]
    public Task A_session_that_closes_while_its_status_is_checked_is_not_admitted(CancellationToken token) =>
        RetryStalledAsync(SessionClosedDuringAdmissionAsync, token, TimeSpan.FromSeconds(20));

    private static async Task SessionClosedDuringAdmissionAsync(CancellationToken token)
    {
        Node remote = Create();
        Node local = Create();
        await using PeerHostScope hosts = new(local.P2P, remote.P2P);
        await hosts.StartAsync(token, remote.P2P, local.P2P);
        // The local status is read after the peer's status arrives and before the peer is recorded.
        ScriptedStatusSource closing = new(_ =>
        {
            if (local.P2P.TryGetEstablishedSession(remote.P2P.LocalPeerId!, out ISession? session))
            {
                session.DisconnectAsync().Wait(TimeSpan.FromSeconds(5));
                SpinWait.SpinUntil(() => local.P2P.SessionClosedToken(session).IsCancellationRequested, TimeSpan.FromSeconds(5));
            }

            return Status;
        });
        TestLogRecorder logger = new();
        PeerManager manager = new(local.P2P, local.Config, closing, new OneLoggerLogManager(new ILogger(logger)));

        bool admitted = await manager.TryAddPeerAsync(LoopbackAddressText(remote.P2P), token);

        if (closing.Requests == 0)
        {
            ThrowIfIdentifyStalled(local.P2P, remote.P2P);
            throw new TimeoutException("the dial never reached the status check");
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(admitted, Is.False, "a peer whose session closed during admission is not reported as admitted");
        Assert.That(manager.PeerCount, Is.Zero, "nor left in the pool");
        Assert.That(logger.Messages, Has.None.StartsWith("Connected to beacon chain peer"), "nor logged as connected");
    }
}
