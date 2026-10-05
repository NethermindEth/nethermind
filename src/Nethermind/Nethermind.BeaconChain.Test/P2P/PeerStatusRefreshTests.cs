// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.Types;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.PeerBandTests;
using static Nethermind.BeaconChain.Test.P2P.PeerHealthCheckRoundTests;

namespace Nethermind.BeaconChain.Test.P2P;

public class PeerStatusRefreshTests
{
    private const ulong ChainAheadBy = 10;
    private const string Reason = "gossip shows the chain past the peers";

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_behind_the_chain_is_asked_for_its_status_at_once_and_at_most_once_per_interval(CancellationToken token)
    {
        (Node client, StatusMessageV2 status) = CreateClient();
        ulong head = status.HeadSlot;
        Node server = CreateNode(new ScriptedStatusSource(n => WithHead(status, n == 1 ? head : head + ChainAheadBy)));
        TestLogRecorder log = new(TestLogLevels.Debug);
        try
        {
            PeerManager peerManager = await PeerHealthCheckRoundTests.StartAndAdmitAsync(client, [server], token, log);
            Assert.That(peerManager.GetBestPeers(head + 1), Is.Empty, "fixture: the admission status is behind the chain");
            // The admission's own request would otherwise hold the first refresh back.
            peerManager.MinStatusRefreshInterval = TimeSpan.Zero;

            Pool(peerManager).RefreshStatusesBehind(head + ChainAheadBy, Reason);
            await PeerSessionNodes.WaitUntilAsync(() => peerManager.GetBestPeers(head + 1).Count == 1, "the peer's status was not asked again", token);
            peerManager.MinStatusRefreshInterval = TimeSpan.FromHours(1);
            Pool(peerManager).RefreshStatusesBehind(head + ChainAheadBy + 1, Reason);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(peerManager.GetBestPeers(head + 1).Single().HeadSlot, Is.EqualTo(head + ChainAheadBy));
                Assert.That(log.Messages.Count(static l => l.Contains(Reason, StringComparison.Ordinal)), Is.EqualTo(1), "one Debug line per trigger that asks, none for one the interval holds back");
            }
        }
        finally
        {
            await PeerHealthCheckRoundTests.DisposeAsync(client, [server]);
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_whose_status_refresh_fails_after_the_chain_moved_past_it_is_offered_for_the_slots_up_to_that_point(CancellationToken token)
    {
        (Node client, StatusMessageV2 status) = CreateClient();
        ulong head = status.HeadSlot;
        Node server = CreateNode(new ScriptedStatusSource(n => n == 1 ? status : throw new Eth2ReqRespException("status refused for the test")));
        try
        {
            PeerManager peerManager = await PeerHealthCheckRoundTests.StartAndAdmitAsync(client, [server], token);
            Assert.That(peerManager.GetBestPeers(head + 1), Is.Empty, "fixture: the admission status is behind the chain");
            peerManager.MinStatusRefreshInterval = TimeSpan.Zero;

            Pool(peerManager).RefreshStatusesBehind(head + ChainAheadBy, Reason);
            await PeerSessionNodes.WaitUntilAsync(() => peerManager.GetBestPeers(head + 1).Count == 1, "a peer whose refresh failed stayed out of range sync", token);

            Assert.That(peerManager.GetBestPeers(head + ChainAheadBy + 1), Is.Empty, "nothing shows the chain past the signalled slot");
        }
        finally
        {
            await PeerHealthCheckRoundTests.DisposeAsync(client, [server]);
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_whose_status_refresh_fails_is_not_offered_past_its_head_when_the_chain_was_not_claimed_past_it(CancellationToken token)
    {
        (Node client, StatusMessageV2 status) = CreateClient();
        ulong head = status.HeadSlot;
        ScriptedStatusSource source = new(n => n == 1 ? status : throw new Eth2ReqRespException("status refused for the test"));
        Node server = CreateNode(source);
        try
        {
            PeerManager peerManager = await PeerHealthCheckRoundTests.StartAndAdmitAsync(client, [server], token);
            peerManager.MinStatusRefreshInterval = TimeSpan.Zero;
            int admissionRequests = source.Requests;

            Pool(peerManager).RefreshStatusesBelow(head + ChainAheadBy, Reason);
            await PeerSessionNodes.WaitUntilAsync(() => source.Requests > admissionRequests, "fixture: the peer's status was not asked again", token);

            // The failure is recorded just after the refused reply, so the peer is watched for a while rather than checked once.
            using CancellationTokenSource watch = CancellationTokenSource.CreateLinkedTokenSource(token);
            watch.CancelAfter(TimeSpan.FromSeconds(1));
            while (!watch.IsCancellationRequested)
            {
                Assert.That(peerManager.GetBestPeers(head + 1), Is.Empty);
                await Task.Delay(20, CancellationToken.None);
            }
        }
        finally
        {
            await PeerHealthCheckRoundTests.DisposeAsync(client, [server]);
        }
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Failed_status_refreshes_do_not_count_toward_dropping_or_banning_the_peer(CancellationToken token)
    {
        (Node client, StatusMessageV2 status) = CreateClient();
        ulong head = status.HeadSlot;
        Node server = CreateNode(new ScriptedStatusSource(n => n == 1 ? status : throw new Eth2ReqRespException("status refused for the test")));
        TestLogRecorder log = new(TestLogLevels.Debug);
        try
        {
            PeerManager peerManager = await PeerHealthCheckRoundTests.StartAndAdmitAsync(client, [server], token, log);
            peerManager.MinStatusRefreshInterval = TimeSpan.Zero;
            const int failedRefreshes = 8;
            await PeerSessionNodes.WaitUntilAsync(() =>
            {
                Pool(peerManager).RefreshStatusesBehind(head + ChainAheadBy, Reason);
                return log.Messages.Count(static l => l.Contains(Reason, StringComparison.Ordinal)) > failedRefreshes;
            }, "the refreshes did not run one after another", token, TimeSpan.FromSeconds(60));

            IBeaconSyncPeer peer = peerManager.GetBestPeers(0).Single();
            int failuresAfterRefreshes = PeerManager.ConsecutiveFailuresForTest(peer);
            await peerManager.RunMaintenanceRoundAsync(token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(failuresAfterRefreshes, Is.Zero);
                Assert.That(peerManager.PeerCount, Is.EqualTo(1), "one failed health check does not drop the peer");
                Assert.That(peerManager.IsBannedForTest(server.P2P.LocalPeerId!.ToString()), Is.False);
            }
        }
        finally
        {
            await PeerHealthCheckRoundTests.DisposeAsync(client, [server]);
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Status_is_refreshed_on_its_own_cadence_between_maintenance_rounds(CancellationToken token)
    {
        (Node client, StatusMessageV2 status) = CreateClient();
        ulong head = status.HeadSlot;
        Node server = CreateNode(new ScriptedStatusSource(n => WithHead(status, n <= 2 ? head : head + ChainAheadBy)));
        // One peer is not under-peered here, so the next maintenance round is a full interval away.
        client.Config.MinPeerCount = 1;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? run = null;
        try
        {
            PeerManager peerManager = await PeerHealthCheckRoundTests.StartAndAdmitAsync(client, [server], token);
            peerManager.StatusRefreshInterval = TimeSpan.FromSeconds(1);
            peerManager.MinStatusRefreshInterval = TimeSpan.Zero;
            Assert.That(peerManager.NextMaintenanceIntervalForTest, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(30)), "fixture: no second maintenance round within the wait");
            run = peerManager.Run(stop.Token);

            await PeerSessionNodes.WaitUntilAsync(() => peerManager.GetBestPeers(0).SingleOrDefault()?.HeadSlot == head + ChainAheadBy, "the status was not refreshed before the next maintenance round", token, TimeSpan.FromSeconds(15));
        }
        finally
        {
            await stop.CancelAsync();
            if (run is not null)
            {
                await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
            }

            await PeerHealthCheckRoundTests.DisposeAsync(client, [server]);
        }
    }

    private static (Node Client, StatusMessageV2 Status) CreateClient()
    {
        Node client = CreateNode();
        SetMatchingStatus(client);
        return (client, client.StatusHolder.CurrentStatus);
    }

    private static IBeaconSyncPeerPool Pool(PeerManager peerManager) => peerManager;
}
