// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Types;
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
        await using Fixture fixture = await Fixture.CreateAsync(token, withUsablePeer: true);
        fixture.Fail(failure == Failure.SessionClosed ? PeerFailureReason.SessionClosed : PeerFailureReason.RequestFailed, failure == Failure.SessionClosed ? 1 : Limit);

        await fixture.Manager.RunMaintenanceRoundAsync(token);
        await fixture.Manager.RunMaintenanceRoundAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.IsSelectable, Is.False, "answering status and ping does not make a peer that fails its requests selectable again");
            Assert.That(fixture.Manager.PeerCount, Is.EqualTo(2), "request failures alone do not drop the peer");
        }
    }

    [TestCase(typeof(TimeoutException), PeerFailureReason.RequestFailed, ExpectedResult = true)]
    [TestCase(typeof(TaskCanceledException), PeerFailureReason.RequestFailed, ExpectedResult = true)]
    [TestCase(typeof(TimeoutException), PeerFailureReason.ProtocolViolation, ExpectedResult = false)]
    [TestCase(typeof(InvalidOperationException), PeerFailureReason.RequestFailed, ExpectedResult = false)]
    [CancelAfter(60_000)]
    public async Task<bool> A_drop_is_for_silence_only_when_the_last_failure_is_a_timeout_and_no_reply_violated_the_protocol(Type exceptionType, PeerFailureReason earlier, CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);
        fixture.Fail(earlier, 1);

        return PeerManager.IsUnresponsiveFailure(fixture.Peer, (Exception)Activator.CreateInstance(exceptionType)!);
    }

    [Test]
    [CancelAfter(90_000)]
    public async Task A_peer_that_stops_answering_is_retained_unless_it_also_violated_the_protocol([Values] bool violated, CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token, hangable: true);
        fixture.Config.FaultDisconnectsBeforeBan = 1;
        fixture.CountTimeoutsAsDrops();
        fixture.Fail(violated ? PeerFailureReason.ProtocolViolation : PeerFailureReason.RequestFailed, Limit - 1);
        string peerId = fixture.Manager.Peers.Single().PeerId;
        fixture.StopAnswering();

        await fixture.Manager.RunMaintenanceRoundAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Manager.PeerCount, Is.EqualTo(violated ? 0 : 1));
            Assert.That(fixture.Manager.IsBannedForTest(peerId), Is.EqualTo(violated), "silence alone must not ban, a violation in the run must");
        }
    }

    // A lone peer that stops answering cannot be told from this node stalling, so its health check timeout is not counted.
    [Test]
    [CancelAfter(120_000)]
    public async Task A_health_check_that_times_out_while_nothing_else_was_answered_is_not_counted(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token, hangable: true);
        fixture.Fail(PeerFailureReason.RequestFailed, 3);
        fixture.StopAnswering();

        await fixture.Manager.RunMaintenanceRoundAsync(token);

        Assert.That(PeerManager.ConsecutiveFailuresForTest(fixture.Peer), Is.EqualTo(3));
    }

    // Kept below the peer floor rather than dropped, but not handed out while its health checks fail.
    [Test]
    [CancelAfter(120_000)]
    public async Task A_silent_peer_kept_at_the_peer_floor_is_out_of_selection(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token, hangable: true, withUsablePeer: true);
        fixture.Manager.CountEveryTimeoutForTest();
        fixture.Fail(PeerFailureReason.RequestFailed, Limit - 1);
        bool selectableBefore = fixture.IsSelectable;
        fixture.StopAnswering();

        await fixture.Manager.RunMaintenanceRoundAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selectableBefore, Is.True, "test setup: below the request-failure limit");
            Assert.That(fixture.Manager.PeerCount, Is.EqualTo(2), "two connected peers are below the floor");
            Assert.That(fixture.IsSelectable, Is.False);
        }

        fixture.ResumeAnswering();
        await fixture.Manager.RunMaintenanceRoundAsync(token);

        Assert.That(fixture.IsSelectable, Is.True, "a passing health check restores selection");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Uncorrelated_timeouts_deprioritise_a_peer_without_dropping_it_even_above_the_floor(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token, withUsablePeer: true);
        fixture.Config.MinPeerCount = 0;
        for (int i = 0; i < Limit * 2; i++)
        {
            await fixture.Manager.HandleHealthFailureAsync(fixture.Peer, new TimeoutException(), fixture.Time.UtcNow.Ticks, token);
        }

        Assert.That(fixture.Manager.PeerCount, Is.EqualTo(2));
        Assert.That(fixture.IsSelectable, Is.False);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Independently_confirmed_timeouts_keep_the_floor_and_do_not_share_the_request_failure_budget([Values] bool keepFloor, CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);
        Node other = CreateNode();
        SetMatchingStatus(other);
        try
        {
            await other.P2P.StartAsync(token);
            Assert.That(await fixture.Manager.TryAddPeerAsync(LoopbackAddress(other.P2P), token), Is.True);
            fixture.Config.MinPeerCount = keepFloor ? 2 : 1;
            long startedAt = fixture.Time.UtcNow.Ticks;
            fixture.Time.Add(TimeSpan.FromSeconds(1));
            await fixture.Manager.RunMaintenanceRoundAsync(token);
            // After the passing health check, which would clear them: sync failures must not count toward the timeout budget.
            fixture.Fail(PeerFailureReason.RequestFailed, Limit);
            for (int i = 0; i < Limit; i++)
            {
                await fixture.Manager.HandleHealthFailureAsync(fixture.Peer, new TimeoutException(), startedAt, token);
                if (i < Limit - 1) Assert.That(fixture.Manager.PeerCount, Is.EqualTo(2));
            }

            Assert.That(fixture.Manager.PeerCount, Is.EqualTo(keepFloor ? 2 : 1));
            if (!keepFloor) Assert.That(await fixture.Manager.TryAddPeerAsync(fixture.Peer.Id, token), Is.False, "a dropped endpoint also observes backoff");
        }
        finally
        {
            await other.P2P.DisposeAsync();
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_lost_session_is_silence_not_a_protocol_violation(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);

        Assert.That(PeerManager.IsUnresponsiveFailure(fixture.Peer, new InvalidOperationException("Channel closed")), Is.True);
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task A_bad_health_check_reply_is_not_excused_by_a_timeout_that_ends_the_run(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token, hangable: true);
        fixture.Config.FaultDisconnectsBeforeBan = 1;
        fixture.CountTimeoutsAsDrops();
        string peerId = fixture.Manager.Peers.Single().PeerId;
        fixture.Break();
        await fixture.Manager.RunMaintenanceRoundAsync(token);
        fixture.StopAnswering();
        fixture.Fail(PeerFailureReason.RequestFailed, Limit - 2);

        await fixture.Manager.RunMaintenanceRoundAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Manager.PeerCount, Is.EqualTo(0), "test setup: the failure limit drops the peer");
            Assert.That(fixture.Manager.IsBannedForTest(peerId), Is.True, "a reply that failed its checks counts even when the last failure is a timeout");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_passing_health_check_forgives_an_earlier_protocol_violation_in_the_run(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);
        fixture.Fail(PeerFailureReason.ProtocolViolation, 1);

        await fixture.Manager.RunMaintenanceRoundAsync(token);

        Assert.That(PeerManager.IsUnresponsiveFailure(fixture.Peer, new TimeoutException()), Is.True);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_below_the_limit_stays_selectable_through_health_checks(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token, withUsablePeer: true);
        fixture.Fail(PeerFailureReason.RequestFailed, Limit - 1);

        await fixture.Manager.RunMaintenanceRoundAsync(token);

        Assert.That(fixture.IsSelectable, Is.True);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_served_request_takes_one_failure_off_so_one_more_failure_returns_the_peer_to_the_limit(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token, withUsablePeer: true);
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
        await using Fixture fixture = await Fixture.CreateAsync(token, withUsablePeer: true);
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
        await using Fixture fixture = await Fixture.CreateAsync(token, withUsablePeer: true);
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
        await using Fixture fixture = await Fixture.CreateAsync(token, withUsablePeer: true);
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

    /// <summary>Serves <paramref name="inner"/> until <see cref="Hang"/>, then blocks each status read until <see cref="Release"/>; <see cref="Break"/> makes each read throw.</summary>
    private sealed class HangableStatusSource(IBeaconChainStatusSource inner) : IBeaconChainStatusSource
    {
        private readonly ManualResetEventSlim _released = new(true);

        private volatile bool _broken;

        public void Hang() => _released.Reset();

        public void Break() => _broken = true;

        public void Release() => _released.Set();

        public StatusMessageV2 CurrentStatus
        {
            get
            {
                _released.Wait();
                if (_broken)
                {
                    throw new InvalidOperationException("status source is broken");
                }

                return inner.CurrentStatus;
            }
        }

        public Hash256 JustifiedRoot => inner.JustifiedRoot;

        public bool ExecutionInSync => inner.ExecutionInSync;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly TimeSpan SilentPeerRequestTimeout = TimeSpan.FromSeconds(3);

        private Node _client = null!;
        private Node _server = null!;
        private Node? _usable;
        private HangableStatusSource? _hang;

        public ManualTimestamper Time { get; } = new();
        public PeerManager Manager { get; private set; } = null!;
        public IBeaconSyncPeer Peer { get; private set; } = null!;

        public bool IsSelectable => Manager.GetBestPeers(0).Contains(Peer);

        public BeaconChainConfig Config => _client.Config;

        /// <summary>Makes the server's status answer block, so the client's requests to it time out.</summary>
        public void StopAnswering() => _hang!.Hang();

        public void ResumeAnswering() => _hang!.Release();

        /// <summary>Makes the server's status answer fail with an error instead of a reply.</summary>
        public void Break() => _hang!.Break();

        /// <param name="withUsablePeer">Connects a second peer that stays under the request-failure limit, so the limit takes <see cref="Peer"/> out of selection.</param>
        public static async Task<Fixture> CreateAsync(CancellationToken token, ulong? serverEarliestAvailableSlot = null, bool hangable = false, bool withUsablePeer = false)
        {
            // A silent peer costs two request timeouts per health check (status v2, then v1); what is counted does not depend on their length.
            Fixture fixture = new() { _client = CreateNode(requestTimeout: hangable ? SilentPeerRequestTimeout : null) };
            if (hangable)
            {
                fixture._hang = new HangableStatusSource(fixture._client.StatusHolder);
            }

            fixture._server = CreateNode(fixture._hang);
            fixture._usable = withUsablePeer ? CreateNode() : null;
            SetMatchingStatus(fixture._usable is null ? new[] { fixture._client, fixture._server } : new[] { fixture._client, fixture._server, fixture._usable });
            if (serverEarliestAvailableSlot is { } earliest)
            {
                fixture._server.StatusHolder.CurrentStatus.EarliestAvailableSlot = earliest;
            }

            await fixture._client.P2P.StartAsync(token);
            await fixture._server.P2P.StartAsync(token);
            fixture.Manager = new PeerManager(fixture._client.P2P, fixture._client.Config, fixture._client.StatusHolder, LimboLogs.Instance, timestamper: fixture.Time);
            Assert.That(await fixture.Manager.TryAddPeerAsync(LoopbackAddress(fixture._server.P2P), token), Is.True);
            if (fixture._usable is not null)
            {
                await fixture._usable.P2P.StartAsync(token);
                Assert.That(await fixture.Manager.TryAddPeerAsync(LoopbackAddress(fixture._usable.P2P), token), Is.True);
            }

            fixture.Peer = fixture.Manager.GetBestPeers(0).Single(p => p.Id == LoopbackAddress(fixture._server.P2P));
            return fixture;
        }

        /// <summary>A lone silent peer is otherwise kept: below the peer floor, and with no other answer its silence could be this node's stall.</summary>
        public void CountTimeoutsAsDrops()
        {
            Config.MinPeerCount = 0;
            Manager.CountEveryTimeoutForTest();
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
            _hang?.Release();
            await _client.P2P.DisposeAsync();
            await _server.P2P.DisposeAsync();
            if (_usable is not null)
            {
                await _usable.P2P.DisposeAsync();
            }
        }
    }
}
