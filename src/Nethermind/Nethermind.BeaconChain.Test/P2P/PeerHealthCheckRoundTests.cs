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
    private const int UnansweringPeers = 3;

    [Test]
    [CancelAfter(60_000)]
    public async Task Unanswering_peers_are_checked_together_and_do_not_delay_a_live_peers_head_refresh(CancellationToken token)
    {
        Node client = CreateNode();
        SetMatchingStatus(client);
        StatusMessageV2 status = client.StatusHolder.CurrentStatus;
        ulong refreshedHead = status.HeadSlot + 1;
        using ManualResetEventSlim roundStarted = new();
        using ManualResetEventSlim release = new();
        // Keyed on the round, not on request order, since an admission may exchange status more than once.
        ScriptedStatusSource[] unansweringStatus = [.. Enumerable.Range(0, UnansweringPeers).Select(_ => new ScriptedStatusSource(request =>
        {
            if (roundStarted.IsSet)
            {
                release.Wait(TimeSpan.FromSeconds(30));
            }

            return status;
        }))];
        ScriptedStatusSource liveStatus = new(request => WithHead(status, roundStarted.IsSet ? refreshedHead : status.HeadSlot));
        Node[] unanswering = [.. unansweringStatus.Select(static s => CreateNode(s))];
        Node live = CreateNode(liveStatus);
        Node[] servers = [.. unanswering, live];

        try
        {
            foreach (Node node in (Node[])[.. servers, client])
            {
                await node.P2P.StartAsync(token);
            }

            PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
            foreach (Node server in servers)
            {
                // Admission is not what this test checks, so a dial whose session the pinned libp2p loses is tried again.
                bool admitted = false;
                for (int attempt = 0; attempt < 3 && !admitted; attempt++)
                {
                    admitted = await peerManager.TryAddPeerAsync(LoopbackAddress(server.P2P), token);
                }

                Assert.That(admitted, Is.True);
            }

            int[] admissionRequests = [.. unansweringStatus.Select(static s => s.Requests)];
            roundStarted.Set();
            Task round = peerManager.RunMaintenanceRoundAsync(token);
            // Well under the 15 s request timeout, so no check of an unanswering peer can have ended before this.
            await PeerSessionNodes.WaitUntilAsync(
                () => unansweringStatus.Select((s, i) => s.Requests > admissionRequests[i]).All(static asked => asked) && LiveHead() == refreshedHead,
                "every unanswering peer is asked before any answers, and the live peer's head is refreshed meanwhile",
                token,
                TimeSpan.FromSeconds(10));
            release.Set();
            await round;

            Assert.That(LiveHead(), Is.EqualTo(refreshedHead));

            ulong? LiveHead() => peerManager.GetBestPeers(0).SingleOrDefault(p => p.Id == LoopbackAddress(live.P2P))?.HeadSlot;
        }
        finally
        {
            release.Set();
            foreach (Node node in (Node[])[.. servers, client])
            {
                await node.P2P.DisposeAsync();
            }
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
