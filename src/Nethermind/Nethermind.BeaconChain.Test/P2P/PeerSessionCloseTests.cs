// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.PeerSessionNodes;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// A peer whose libp2p session closed can never answer again: it leaves the pool at once, without a failure counted against it,
/// so selection and the failure budget never see it and the dial loop can replace it.
/// </summary>
public class PeerSessionCloseTests
{
    private const string PeerAddress = "/ip4/10.0.0.1/tcp/9000/p2p/16Uiu2HAmPeer";

    // Well under the admission wait's 5 s poll, so only the wake on removal can end the wait in time.
    private static readonly TimeSpan ReplacementBound = TimeSpan.FromSeconds(2);

    [Test]
    [CancelAfter(30_000)]
    public async Task A_peer_whose_session_closes_leaves_the_pool_at_once_without_a_failure_and_a_replacement_dial_is_let_through(CancellationToken token)
    {
        Node node = Create();
        node.Config.TargetPeerCount = 1;
        node.Config.FaultDisconnectsBeforeBan = 1;
        await using (node.P2P)
        {
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
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_peer_whose_session_closed_before_it_was_recorded_never_stays_in_the_pool(CancellationToken token)
    {
        Node node = Create();
        await using (node.P2P)
        {
            await node.P2P.StartAsync(token);
            PeerManager manager = node.CreatePeerManager();
            LocalPeer.Session session = RequestFailureCauseTests.AddWedgedSession(node.P2P);
            await session.DisconnectAsync();

            manager.AddPeerForTest(session, PeerAddress, Status);

            Assert.That(manager.PeerCount, Is.Zero);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Dropping_a_peer_whose_address_a_newer_peer_took_leaves_the_newer_one_in_the_pool(CancellationToken token)
    {
        Node node = Create();
        await using (node.P2P)
        {
            PeerManager manager = node.CreatePeerManager();
            IBeaconSyncPeer older = manager.AddPeerForTest(Substitute.For<ISession>(), PeerAddress, Status);
            IBeaconSyncPeer newer = manager.AddPeerForTest(Substitute.For<ISession>(), PeerAddress, Status);

            for (int i = 0; i < 8; i++)
            {
                await manager.HandleHealthFailureAsync(older, new InvalidDataException("bad reply"), startedAt: 0, token);
            }

            Assert.That(manager.GetBestPeers(0), Is.EqualTo(new[] { newer }), "a drop that started before the address changed hands removes only the peer it names");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public Task A_session_that_closes_while_its_status_is_checked_is_not_admitted(CancellationToken token) =>
        RetryStalledAsync(SessionClosedDuringAdmissionAsync, token, TimeSpan.FromSeconds(20));

    private static async Task<bool> SessionClosedDuringAdmissionAsync(CancellationToken token)
    {
        Node remote = Create();
        Node local = Create();
        await using (local.P2P)
        await using (remote.P2P)
        {
            await remote.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
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
            PeerManager manager = new(local.P2P, local.Config, closing, LimboLogs.Instance);

            bool admitted = await manager.TryAddPeerAsync(LoopbackAddressText(remote.P2P), token);

            if (closing.Requests == 0)
            {
                ThrowIfIdentifyStalled(local.P2P, remote.P2P);
                throw new TimeoutException("the dial never reached the status check");
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(admitted, Is.False, "a peer whose session closed during admission is not reported as admitted");
                Assert.That(manager.PeerCount, Is.Zero, "nor left in the pool");
            }
        }

        return true;
    }
}
