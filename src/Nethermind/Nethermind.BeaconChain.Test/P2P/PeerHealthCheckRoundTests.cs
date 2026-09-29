// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.PeerBandTests;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// Range sync picks peers by the head each last reported over <c>status</c>, and a maintenance round is what refreshes it,
/// so peers that never answer must not hold the round up for one request timeout each.
/// </summary>
public class PeerHealthCheckRoundTests
{
    private const int MaxConcurrentChecks = 8;

    // Under the 15 s request timeout, so a status held this long still answers and a check that ran alone shows only as a missed rendezvous.
    private static readonly TimeSpan RendezvousWait = TimeSpan.FromSeconds(10);

    [Test]
    [CancelAfter(120_000)]
    public async Task Unanswering_peers_are_checked_together_with_a_live_peer_whose_head_is_refreshed(CancellationToken token)
    {
        const int unansweringPeers = 3;
        Node client = CreateNode();
        SetMatchingStatus(client);
        StatusMessageV2 status = client.StatusHolder.CurrentStatus;
        ulong refreshedHead = status.HeadSlot + 1;
        using ManualResetEventSlim roundStarted = new();
        using ManualResetEventSlim allAsked = new();
        int asked = 0;
        int missedRendezvous = 0;
        // Each round status is held until every peer has been asked, which only a round checking them together reaches.
        StatusMessageV2 HeldUntilAllAsked(StatusMessageV2 answer)
        {
            if (roundStarted.IsSet)
            {
                if (Interlocked.Increment(ref asked) == unansweringPeers + 1)
                {
                    allAsked.Set();
                }

                if (!allAsked.Wait(RendezvousWait))
                {
                    Interlocked.Increment(ref missedRendezvous);
                }
            }

            return answer;
        }

        Node[] unanswering = [.. Enumerable.Range(0, unansweringPeers).Select(_ => CreateNode(new ScriptedStatusSource(_ => HeldUntilAllAsked(status))))];
        Node live = CreateNode(new ScriptedStatusSource(_ => HeldUntilAllAsked(WithHead(status, roundStarted.IsSet ? refreshedHead : status.HeadSlot))));
        Node[] servers = [.. unanswering, live];

        try
        {
            PeerManager peerManager = await StartAndAdmitAsync(client, servers, token);
            roundStarted.Set();
            await peerManager.RunMaintenanceRoundAsync(token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(missedRendezvous, Is.Zero, "every peer's status is requested before any is answered");
                Assert.That(peerManager.GetBestPeers(0).SingleOrDefault(p => p.Id == LoopbackAddress(live.P2P))?.HeadSlot, Is.EqualTo(refreshedHead));
            }
        }
        finally
        {
            allAsked.Set();
            await DisposeAsync(client, servers);
        }
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task A_round_checks_at_most_eight_peers_at_once_and_stops_when_cancelled(CancellationToken token)
    {
        const int peers = 2 * MaxConcurrentChecks;
        Node client = CreateNode();
        SetMatchingStatus(client);
        StatusMessageV2 status = client.StatusHolder.CurrentStatus;
        using ManualResetEventSlim roundStarted = new();
        using ManualResetEventSlim release = new();
        int inFlight = 0;
        int maxInFlight = 0;
        ScriptedStatusSource held = new(_ =>
        {
            if (roundStarted.IsSet)
            {
                int now = Interlocked.Increment(ref inFlight);
                for (int seen = Volatile.Read(ref maxInFlight); now > seen; seen = Volatile.Read(ref maxInFlight))
                {
                    Interlocked.CompareExchange(ref maxInFlight, now, seen);
                }

                release.Wait(TimeSpan.FromSeconds(30));
                Interlocked.Decrement(ref inFlight);
            }

            return status;
        });
        Node[] servers = [.. Enumerable.Range(0, peers).Select(_ => CreateNode(held))];

        try
        {
            PeerManager peerManager = await StartAndAdmitAsync(client, servers, token);
            using CancellationTokenSource roundCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            roundStarted.Set();
            Task round = peerManager.RunMaintenanceRoundAsync(roundCancellation.Token);
            await PeerSessionNodes.WaitUntilAsync(() => Volatile.Read(ref inFlight) >= MaxConcurrentChecks, "the round never asked enough peers at once", token);
            // Long enough for a check past the bound to have been started; the held ones cannot finish meanwhile.
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            int observedMax = Volatile.Read(ref maxInFlight);
            await roundCancellation.CancelAsync();
            // Well under the request timeout, so only the round's token can end the held checks in time.
            bool endedPromptly = await Task.WhenAny(round, Task.Delay(TimeSpan.FromSeconds(5), token)) == round;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(observedMax, Is.EqualTo(MaxConcurrentChecks));
                Assert.That(endedPromptly, Is.True, "cancelling the round ends the checks in flight");
                Assert.That(round.IsCanceled, Is.True);
                Assert.That(peerManager.PeerCount, Is.EqualTo(peers), "a cancelled check does not count against its peer");
            }
        }
        finally
        {
            release.Set();
            await DisposeAsync(client, servers);
        }
    }

    private static async Task<PeerManager> StartAndAdmitAsync(Node client, Node[] servers, CancellationToken token)
    {
        foreach (Node node in (Node[])[.. servers, client])
        {
            await node.P2P.StartAsync(token);
        }

        PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
        foreach (Node server in servers)
        {
            // Admission is not what these tests check, so a dial whose session the pinned libp2p loses is tried again.
            bool admitted = false;
            for (int attempt = 0; attempt < 3 && !admitted; attempt++)
            {
                admitted = await peerManager.TryAddPeerAsync(LoopbackAddress(server.P2P), token);
            }

            Assert.That(admitted, Is.True);
        }

        return peerManager;
    }

    private static async Task DisposeAsync(Node client, Node[] servers)
    {
        foreach (Node node in (Node[])[.. servers, client])
        {
            await node.P2P.DisposeAsync();
        }
    }

    private static StatusMessageV2 WithHead(StatusMessageV2 status, ulong headSlot) => new()
    {
        ForkDigest = status.ForkDigest,
        FinalizedRoot = status.FinalizedRoot,
        FinalizedEpoch = status.FinalizedEpoch,
        HeadRoot = status.HeadRoot,
        HeadSlot = headSlot,
        EarliestAvailableSlot = status.EarliestAvailableSlot,
    };
}
