// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using static Nethermind.BeaconChain.Test.P2P.PeerBandTests;

namespace Nethermind.BeaconChain.Test.P2P;

public class PeerRequestCooldownTests
{
    private const ulong HeadLead = 100;

    // A closed session puts the peer out of selection outright, so its place in the order cannot be observed.
    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_that_failed_a_request_is_offered_after_the_others_until_the_cooldown_ends([Values(PeerFailureReason.RequestFailed, PeerFailureReason.ProtocolViolation)] PeerFailureReason reason, CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);
        Assert.That(fixture.Listed, Is.EqualTo(new[] { fixture.Ahead.Id, fixture.Behind.Id }), "test setup: the peer with the best head is first");

        fixture.Ahead.ReportFailure(reason);
        string[] duringCooldown = fixture.Listed;
        fixture.Time.Add(PeerManager.RequestFailureCooldown - TimeSpan.FromSeconds(1));
        string[] justBeforeTheEnd = fixture.Listed;
        fixture.Time.Add(TimeSpan.FromSeconds(1));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(duringCooldown, Is.EqualTo(new[] { fixture.Behind.Id, fixture.Ahead.Id }), "the next batch does not pick the peer that just failed while another peer can serve");
        Assert.That(justBeforeTheEnd, Is.EqualTo(duringCooldown));
        Assert.That(fixture.Listed, Is.EqualTo(new[] { fixture.Ahead.Id, fixture.Behind.Id }), "the cooldown is not a ban");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_is_offered_after_the_others_for_thirty_seconds_after_a_failed_request(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);
        fixture.Ahead.ReportFailure(PeerFailureReason.RequestFailed);

        fixture.Time.Add(TimeSpan.FromSeconds(29));
        string[] at29 = fixture.Listed;
        fixture.Time.Add(TimeSpan.FromSeconds(1));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(at29, Is.EqualTo(new[] { fixture.Behind.Id, fixture.Ahead.Id }));
        Assert.That(fixture.Listed, Is.EqualTo(new[] { fixture.Ahead.Id, fixture.Behind.Id }));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_in_cooldown_that_is_the_only_one_is_still_offered(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token, connectBehind: false);

        fixture.Ahead.ReportFailure(PeerFailureReason.RequestFailed);

        Assert.That(fixture.Listed, Is.EqualTo(new[] { fixture.Ahead.Id }), "a lone custodian is used, not skipped");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_served_request_does_not_end_the_cooldown(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);
        fixture.Ahead.ReportFailure(PeerFailureReason.RequestFailed);

        await fixture.Ahead.RequestBlocksByRootAsync([Hash256.Zero], token);

        Assert.That(fixture.Listed, Is.EqualTo(new[] { fixture.Behind.Id, fixture.Ahead.Id }));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task An_inbound_violation_does_not_put_the_peer_behind_others(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);

        Assert.That(fixture.Manager.TryReportInboundViolation(fixture.AheadNode.P2P.LocalPeerId!, "bytes after the request"), Is.True, "test setup: the peer is connected");

        Assert.That(fixture.Listed, Is.EqualTo(new[] { fixture.Ahead.Id, fixture.Behind.Id }));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Peers_in_cooldown_keep_the_order_of_their_head_slots(CancellationToken token)
    {
        await using Fixture fixture = await Fixture.CreateAsync(token);

        fixture.Ahead.ReportFailure(PeerFailureReason.RequestFailed);
        fixture.Behind.ReportFailure(PeerFailureReason.RequestFailed);

        Assert.That(fixture.Listed, Is.EqualTo(new[] { fixture.Ahead.Id, fixture.Behind.Id }));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Node _client = null!;

        public ManualTimestamper Time { get; } = new();
        public PeerManager Manager { get; private set; } = null!;
        public Node AheadNode { get; private set; } = null!;
        private Node BehindNode { get; set; } = null!;
        public IBeaconSyncPeer Ahead { get; private set; } = null!;
        public IBeaconSyncPeer Behind { get; private set; } = null!;

        public string[] Listed => [.. Manager.GetBestPeers(0).Select(static p => p.Id)];

        public static async Task<Fixture> CreateAsync(CancellationToken token, bool connectBehind = true)
        {
            Fixture fixture = new() { _client = CreateNode(), AheadNode = CreateNode(), BehindNode = CreateNode() };
            SetMatchingStatus(fixture._client, fixture.AheadNode, fixture.BehindNode);
            fixture.AheadNode.StatusHolder.CurrentStatus.HeadSlot += HeadLead;
            await fixture._client.P2P.StartAsync(token);
            await fixture.AheadNode.P2P.StartAsync(token);
            await fixture.BehindNode.P2P.StartAsync(token);
            fixture.Manager = new PeerManager(fixture._client.P2P, fixture._client.Config, fixture._client.StatusHolder, LimboLogs.Instance, timestamper: fixture.Time);
            Assert.That(await fixture.Manager.TryAddPeerAsync(LoopbackAddress(fixture.AheadNode.P2P), token), Is.True);
            fixture.Ahead = fixture.Manager.GetBestPeers(0).Single();
            if (connectBehind)
            {
                Assert.That(await fixture.Manager.TryAddPeerAsync(LoopbackAddress(fixture.BehindNode.P2P), token), Is.True);
                fixture.Behind = fixture.Manager.GetBestPeers(0).Single(p => p != fixture.Ahead);
            }

            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await _client.P2P.DisposeAsync();
            await AheadNode.P2P.DisposeAsync();
            await BehindNode.P2P.DisposeAsync();
        }
    }
}
