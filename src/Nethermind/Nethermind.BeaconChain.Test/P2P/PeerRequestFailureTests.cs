// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.PeerBandTests;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// A peer can answer <c>status</c> and <c>ping</c> and still time out every sync request, so a passing health check must not
/// clear the failures its requests earned; only a served request or the passing of time does.
/// </summary>
public class PeerRequestFailureTests
{
    // PeerManager's failure limit.
    private const int Limit = 8;

    public enum Failure
    {
        RequestsTimeOut,
        SessionClosed,
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_failing_its_requests_stays_connected_but_out_of_selection_through_passing_health_checks([Values] Failure failure, CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);
        fixture.Fail(failure == Failure.SessionClosed ? PeerFailureReason.SessionClosed : PeerFailureReason.RequestFailed, failure == Failure.SessionClosed ? 1 : Limit);

        await fixture.Manager.RunMaintenanceRoundAsync(token);
        await fixture.Manager.RunMaintenanceRoundAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.IsSelectable, Is.False, "answering status and ping does not make a peer that fails its requests selectable again");
            Assert.That(fixture.Manager.PeerCount, Is.EqualTo(1), "request failures alone do not drop the peer");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_below_the_limit_stays_selectable_through_health_checks(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);
        fixture.Fail(PeerFailureReason.RequestFailed, Limit - 1);

        await fixture.Manager.RunMaintenanceRoundAsync(token);

        Assert.That(fixture.IsSelectable, Is.True);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_served_request_takes_one_failure_off_so_one_more_failure_returns_the_peer_to_the_limit(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);
        fixture.Fail(PeerFailureReason.RequestFailed, Limit + 5);
        Assert.That(fixture.IsSelectable, Is.False);

        await fixture.Peer.RequestBlocksByRootAsync([Hash256.Zero], token);
        bool selectableAfterServing = fixture.IsSelectable;
        fixture.Fail(PeerFailureReason.RequestFailed, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selectableAfterServing, Is.True, "failures are held at the limit, so one served request readmits the peer");
            Assert.That(fixture.IsSelectable, Is.False);
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Request_failures_decay_one_per_interval(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);
        fixture.Fail(PeerFailureReason.RequestFailed, Limit);

        fixture.Time.Add(PeerManager.RequestFailureDecayInterval - TimeSpan.FromSeconds(1));
        bool selectableBeforeTheInterval = fixture.IsSelectable;
        fixture.Time.Add(TimeSpan.FromSeconds(1));
        bool selectableAfterTheInterval = fixture.IsSelectable;
        fixture.Fail(PeerFailureReason.RequestFailed, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selectableBeforeTheInterval, Is.False);
            Assert.That(selectableAfterTheInterval, Is.True, "a peer at the limit is tried again once one failure has decayed");
            Assert.That(fixture.IsSelectable, Is.False, "and is out again when that request fails");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_stays_out_for_a_full_interval_after_its_latest_failure_however_slowly_the_failures_came(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);
        fixture.Fail(PeerFailureReason.RequestFailed, 1);
        fixture.Time.Add(PeerManager.RequestFailureDecayInterval - TimeSpan.FromSeconds(1));
        fixture.Fail(PeerFailureReason.RequestFailed, Limit - 1);

        fixture.Time.Add(TimeSpan.FromSeconds(2));
        bool selectableSoonAfterTheLimit = fixture.IsSelectable;
        fixture.Time.Add(PeerManager.RequestFailureDecayInterval);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selectableSoonAfterTheLimit, Is.False, "the interval counts from the latest failure, so a peer that just reached the limit is not tried in the next round");
            Assert.That(fixture.IsSelectable, Is.True);
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_whose_requests_really_fail_leaves_selection_because_a_failed_request_earns_no_credit(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        for (int i = 0; i < Limit; i++)
        {
            Exception? failure = null;
            try
            {
                await fixture.Peer.RequestBlocksByRangeAsync(0, 1, cancelled.Token);
            }
            catch (Exception e)
            {
                failure = e;
                fixture.Peer.ReportFailure(PeerFailureClassifier.Classify(e));
            }

            Assert.That(failure, Is.Not.Null, "fixture: the request must fail");
        }

        await fixture.Manager.RunMaintenanceRoundAsync(token);

        Assert.That(fixture.IsSelectable, Is.False, "eight failed requests, none credited, take the peer out of selection");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_protocol_violation_is_not_cancelled_by_the_served_request_that_carried_it(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);

        for (int i = 0; i < Limit + 2; i++)
        {
            await fixture.Peer.RequestBlocksByRootAsync([Hash256.Zero], token);
            fixture.Peer.ReportFailure(PeerFailureReason.ProtocolViolation);
        }

        Assert.That(fixture.IsSelectable, Is.False, "a peer that answers every request with content that fails a check is not selected forever");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_reports_the_earliest_available_slot_of_its_status(CancellationToken token)
    {
        const ulong earliestAvailableSlot = 12_345;
        await using Fixture fixture = await Fixture.CreateAsync(token, earliestAvailableSlot);

        Assert.That(fixture.Peer.EarliestAvailableSlot, Is.EqualTo(earliestAvailableSlot));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Node _client = null!;
        private Node _server = null!;

        public ManualTimestamper Time { get; } = new();
        public PeerManager Manager { get; private set; } = null!;
        public IBeaconSyncPeer Peer { get; private set; } = null!;

        public bool IsSelectable => Manager.GetBestPeers(0).Contains(Peer);

        public static async Task<Fixture> CreateAsync(CancellationToken token, ulong? serverEarliestAvailableSlot = null)
        {
            Fixture fixture = new() { _client = CreateNode(), _server = CreateNode() };
            SetMatchingStatus(fixture._client, fixture._server);
            if (serverEarliestAvailableSlot is { } earliest)
            {
                fixture._server.StatusHolder.CurrentStatus.EarliestAvailableSlot = earliest;
            }

            await fixture._client.P2P.StartAsync(token);
            await fixture._server.P2P.StartAsync(token);
            fixture.Manager = new PeerManager(fixture._client.P2P, fixture._client.Config, fixture._client.StatusHolder, LimboLogs.Instance, timestamper: fixture.Time);
            Assert.That(await fixture.Manager.TryAddPeerAsync(LoopbackAddress(fixture._server.P2P), token), Is.True);
            fixture.Peer = fixture.Manager.GetBestPeers(0).Single();
            return fixture;
        }

        public void Fail(PeerFailureReason reason, int times)
        {
            for (int i = 0; i < times; i++)
            {
                Peer.ReportFailure(reason);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _client.P2P.DisposeAsync();
            await _server.P2P.DisposeAsync();
        }
    }
}
