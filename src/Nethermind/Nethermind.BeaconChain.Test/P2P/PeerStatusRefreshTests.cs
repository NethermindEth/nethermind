// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.Types;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.PeerBandTests;
using static Nethermind.BeaconChain.Test.P2P.PeerHealthCheckRoundTests;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// Range sync asks only peers whose last <c>status</c> head covers the slot it needs, so a status that is never asked again
/// leaves every peer "behind" while the chain moves on; phase0/p2p-interface.md Status lets a client ask again to learn a higher head.
/// </summary>
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
        LineCollector log = new();
        try
        {
            PeerManager peerManager = await PeerHealthCheckRoundTests.StartAndAdmitAsync(client, [server], token, log.Manager);
            Assert.That(peerManager.GetBestPeers(head + 1), Is.Empty, "fixture: the admission status is behind the chain");
            // The admission's own request would otherwise hold the first refresh back.
            peerManager.MinStatusRefreshInterval = TimeSpan.Zero;

            Pool(peerManager).RefreshStatusesBehind(head + ChainAheadBy, Reason);
            await WaitUntilAsync(() => peerManager.GetBestPeers(head + 1).Count == 1, token, "the peer's status was not asked again");
            peerManager.MinStatusRefreshInterval = TimeSpan.FromHours(1);
            Pool(peerManager).RefreshStatusesBehind(head + ChainAheadBy + 1, Reason);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(peerManager.GetBestPeers(head + 1).Single().HeadSlot, Is.EqualTo(head + ChainAheadBy));
                Assert.That(log.Lines.Count(static l => l.Contains(Reason, StringComparison.Ordinal)), Is.EqualTo(1), "one Debug line per trigger that asks, none for one the interval holds back");
            }
        }
        finally
        {
            await PeerHealthCheckRoundTests.DisposeAsync(client, [server]);
        }
    }

    // The peer that answered by range while its status timed out is what ended the Hoodi stall, so a failed refresh must not hide it.
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
            await WaitUntilAsync(() => peerManager.GetBestPeers(head + 1).Count == 1, token, "a peer whose refresh failed stayed out of range sync");

            Assert.That(peerManager.GetBestPeers(head + ChainAheadBy + 1), Is.Empty, "nothing shows the chain past the signalled slot");
        }
        finally
        {
            await PeerHealthCheckRoundTests.DisposeAsync(client, [server]);
        }
    }

    // Silence never counts toward a ban (the health check's rule), and a refused refresh is the same request asked outside it.
    [Test]
    [CancelAfter(120_000)]
    public async Task Failed_status_refreshes_do_not_count_toward_dropping_or_banning_the_peer(CancellationToken token)
    {
        (Node client, StatusMessageV2 status) = CreateClient();
        ulong head = status.HeadSlot;
        Node server = CreateNode(new ScriptedStatusSource(n => n == 1 ? status : throw new Eth2ReqRespException("status refused for the test")));
        LineCollector log = new();
        try
        {
            PeerManager peerManager = await PeerHealthCheckRoundTests.StartAndAdmitAsync(client, [server], token, log.Manager);
            peerManager.MinStatusRefreshInterval = TimeSpan.Zero;
            // A refresh starts only once the one before it ended, so the ninth start means eight refreshes failed.
            const int failedRefreshes = 8;
            await WaitUntilAsync(() =>
            {
                Pool(peerManager).RefreshStatusesBehind(head + ChainAheadBy, Reason);
                return log.Lines.Count(static l => l.Contains(Reason, StringComparison.Ordinal)) > failedRefreshes;
            }, token, "the refreshes did not run one after another", TimeSpan.FromSeconds(60));

            IBeaconSyncPeer peer = peerManager.GetBestPeers(0).Single();
            int failuresAfterRefreshes = PeerManager.ConsecutiveFailuresForTest(peer);
            // A refused health check is a bad reply and counts; it must be the first, not the ninth.
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
        // The admission and the first round's health check see the old head; only a later request sees the new one.
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

            await WaitUntilAsync(() => peerManager.GetBestPeers(0).SingleOrDefault()?.HeadSlot == head + ChainAheadBy, token, "the status was not refreshed before the next maintenance round", TimeSpan.FromSeconds(15));
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

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token, string failure, TimeSpan? bound = null)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(bound ?? TimeSpan.FromSeconds(20));
        while (!condition())
        {
            if (timeout.IsCancellationRequested)
            {
                Assert.Fail(failure);
            }

            await Task.Delay(20, CancellationToken.None);
        }
    }

    /// <summary>Keeps every Debug line; safe to write from concurrent requests.</summary>
    private sealed class LineCollector : InterfaceLogger
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public ILogManager Manager => new OneLoggerLogManager(new ILogger(this));

        public string[] Lines => [.. _lines];

        public bool IsInfo => false;
        public bool IsWarn => false;
        public bool IsDebug => true;
        public bool IsTrace => false;
        public bool IsError => false;

        public void Debug(string text) => _lines.Enqueue(text);

        public void Info(string text) { }
        public void Warn(string text) { }
        public void Trace(string text) { }
        public void Error(string text, Exception? ex = null) { }
    }
}
