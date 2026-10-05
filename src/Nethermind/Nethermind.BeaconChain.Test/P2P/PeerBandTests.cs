// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Reflection;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NSubstitute;

namespace Nethermind.BeaconChain.Test.P2P;

public class PeerBandTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private const ulong AnchorSlot = 13_410_304;

    [TestCase("/ip4/1.2.3.4/tcp/9000/p2p/16Uiu2HAm", ExpectedResult = "16Uiu2HAm")]
    public string Peer_id_is_extracted_from_the_p2p_multiaddr_component(string address) =>
        PeerManager.ExtractPeerIdForTest(address);

    [TestCase("/ip4/1.2.3.4/tcp/9000")]
    [TestCase("")]
    public void An_address_without_a_peer_id_has_no_key(string address) =>
        Assert.That(() => PeerManager.ExtractPeerIdForTest(address), Throws.ArgumentException);

    [TestCase("not a multiaddr")]
    [TestCase("/ip4/1.2.3.4/tcp/9000")]
    [TestCase("/p2p/16Uiu2HAkyxG4bkiFUNXPANdX7n13Lz8A2WsDyNkAyJ1Lfs6AXD2e")]
    [TestCase("/dns4/example.org/p2p/16Uiu2HAkyxG4bkiFUNXPANdX7n13Lz8A2WsDyNkAyJ1Lfs6AXD2e")]
    [TestCase("/dns4/example.org/udp/9000/p2p/16Uiu2HAkyxG4bkiFUNXPANdX7n13Lz8A2WsDyNkAyJ1Lfs6AXD2e")]
    [TestCase("/dns4/example.org/tcp/9000/ws/p2p/16Uiu2HAkyxG4bkiFUNXPANdX7n13Lz8A2WsDyNkAyJ1Lfs6AXD2e")]
    [TestCase("/dns4/example.org/tcp/9000/wss/p2p/16Uiu2HAkyxG4bkiFUNXPANdX7n13Lz8A2WsDyNkAyJ1Lfs6AXD2e")]
    [TestCase("/dnsaddr/example.org/p2p/16Uiu2HAkyxG4bkiFUNXPANdX7n13Lz8A2WsDyNkAyJ1Lfs6AXD2e")]
    public void A_static_peer_that_cannot_be_dialed_is_a_configuration_error(string address)
    {
        Node node = CreateNode();
        node.Config.StaticPeers = address;

        Assert.That(() => new PeerManager(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance), Throws.TypeOf<InvalidConfigurationException>());
    }

    [Test]
    public void A_static_peer_with_a_dns_name_and_peer_id_is_accepted([Values("dns", "dns4", "dns6")] string protocol)
    {
        Node node = CreateNode();
        node.Config.StaticPeers = $"/{protocol}/beacon.invalid/tcp/9000/p2p/16Uiu2HAkyxG4bkiFUNXPANdX7n13Lz8A2WsDyNkAyJ1Lfs6AXD2e";

        Assert.That(() => new PeerManager(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance), Throws.Nothing);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_static_dns_peer_is_retried_after_name_resolution_fails(CancellationToken token)
    {
        Node server = CreateNode();
        Node client = CreateNode();
        SetMatchingStatus(server, client);
        await using PeerHostScope nodes = new(client.P2P, server.P2P);
        await nodes.StartAsync(token, server.P2P, client.P2P);

        const string hostname = "beacon.invalid";
        int queries = 0;
        IDnsLookup lookup = Substitute.For<IDnsLookup>();
        lookup.QueryAAsync(hostname).Returns(_ => Interlocked.Increment(ref queries) == 1
            ? Task.FromException<IEnumerable<IPAddress>>(new InvalidOperationException("injected name resolution failure"))
            : Task.FromResult<IEnumerable<IPAddress>>([IPAddress.Loopback]));
        FieldInfo resolver = typeof(LocalPeer).GetField("_multiaddrResolver", BindingFlags.Instance | BindingFlags.NonPublic)!;
        resolver.SetValue(client.P2P.LocalPeerForTest!, new MultiaddrResolver(lookup));
        client.Config.StaticPeers = LoopbackAddress(server.P2P).Replace("/ip4/127.0.0.1/", $"/dns4/{hostname}/", StringComparison.Ordinal);
        Assert.That(client.Config.StaticPeers, Does.StartWith($"/dns4/{hostname}/"));
        PeerManager manager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);

        await manager.RunMaintenanceRoundAsync(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queries, Is.EqualTo(1));
            Assert.That(manager.PeerCount, Is.Zero);
            Assert.That(client.P2P.SessionCountForTest, Is.Zero);
        }

        await manager.RunMaintenanceRoundAsync(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queries, Is.EqualTo(2), "the same static hostname must be resolved again");
            Assert.That(manager.PeerCount, Is.EqualTo(1));
            Assert.That(manager.GetBestPeers(0).Single().HeadSlot, Is.EqualTo(AnchorSlot));
            Assert.That(client.P2P.LocalPeerForTest!.Sessions.Single().RemoteAddress.GetPeerId(), Is.EqualTo(server.P2P.LocalPeerId));
        }
        ulong sequence = await client.P2P.PingAsync(client.P2P.LocalPeerForTest!.Sessions.Single(), token);
        Assert.That(sequence, Is.EqualTo(server.Metadata.Current.SeqNumber));
    }

    [Test]
    public void A_static_peer_with_an_ip_address_and_peer_id_is_accepted()
    {
        Node node = CreateNode();
        node.Config.StaticPeers = "/ip4/1.2.3.4/tcp/9000/p2p/16Uiu2HAkyxG4bkiFUNXPANdX7n13Lz8A2WsDyNkAyJ1Lfs6AXD2e, /ip6/::1/tcp/9000/p2p/16Uiu2HAkyxG4bkiFUNXPANdX7n13Lz8A2WsDyNkAyJ1Lfs6AXD2e";

        Assert.That(() => new PeerManager(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance), Throws.Nothing);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_dial_address_that_does_not_decode_is_refused_without_throwing(CancellationToken token)
    {
        Node node = CreateNode();
        await using PeerHostScope nodes = new(node.P2P);
        await nodes.StartAsync(token, node.P2P);
        PeerManager peerManager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance);

        Assert.That(await peerManager.TryAddPeerAsync("/ip4/127.0.0.1/tcp/1/p2p/not-a-peer-id", token), Is.False);
    }

    /// <summary>Info drop logs name the cause, not an exception type that log watchers interpret as a crash.</summary>
    [TestCase(typeof(TaskCanceledException), ExpectedResult = "request timed out")]
    [TestCase(typeof(OperationCanceledException), ExpectedResult = "request timed out")]
    [TestCase(typeof(TimeoutException), ExpectedResult = "request timed out")]
    [TestCase(typeof(ReqRespTimeoutException), ExpectedResult = "timed out after 16 s waiting for the channel to open: the request budget ran out")]
    [TestCase(typeof(InvalidOperationException), ExpectedResult = "the peer sent garbage")]
    public string A_drop_names_its_cause_rather_than_the_exception_type(Type exceptionType) =>
        PeerManager.DescribeFailure(exceptionType == typeof(InvalidOperationException) ? new InvalidOperationException("the peer sent garbage")
            : exceptionType == typeof(ReqRespTimeoutException) ? new ReqRespTimeoutException("timed out after 16 s waiting for the channel to open: the request budget ran out")
            : (Exception)Activator.CreateInstance(exceptionType)!);

    [Test]
    [CancelAfter(30_000)]
    public async Task Metrics_peer_gauge_and_counters_follow_an_admission_and_a_drop(CancellationToken token)
    {
        Node client = CreateNode();
        Node server = CreateNode();
        SetMatchingStatus(client, server);
        await using PeerHostScope nodes = new(client.P2P, server.P2P);
        await nodes.StartAsync(token, client.P2P, server.P2P);
        PeerManager manager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
        ulong connectedBefore = Metrics.BeaconChainPeersConnected;
        ulong droppedBefore = Metrics.BeaconChainPeersDropped;

        Assert.That(await manager.TryAddPeerAsync(LoopbackAddress(server.P2P), token), Is.True);
        (int Pool, int Gauge, ulong Connected) admitted = (manager.PeerCount, Metrics.BeaconChainPeerCount, Metrics.BeaconChainPeersConnected);

        IBeaconSyncPeer peer = manager.GetBestPeers(0).Single();
        for (int i = 0; i < 8; i++) await manager.HandleHealthFailureAsync(peer, new Eth2ReqRespException("malformed reply"), 0, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(admitted, Is.EqualTo((1, 1, connectedBefore + 1)), "after the admission");
        Assert.That((manager.PeerCount, Metrics.BeaconChainPeerCount), Is.EqualTo((0, 0)), "after the drop");
        Assert.That(Metrics.BeaconChainPeersDropped, Is.EqualTo(droppedBefore + 1));
    }

    [TestCaseSource(nameof(BanTransitions))]
    public void Disconnects_follow_the_ban_transition_policy(int threshold, Action<PeerManager, ManualTimestamper>[] stages)
    {
        ManualTimestamper time = new();
        PeerManager manager = NewManagerWithoutSessions(threshold, time);
        foreach (Action<PeerManager, ManualTimestamper> stage in stages)
        {
            stage(manager, time);
        }
    }

    private static IEnumerable<TestCaseData> BanTransitions()
    {
        TimeSpan banTime = TimeSpan.FromMinutes(new BeaconChainConfig().PeerBanMinutes);
        yield return BanCase("A_single_fault_disconnect_below_the_threshold_does_not_ban", 3,
            Fault(messages: 10), Banned(false));
        yield return BanCase("Consecutive_fault_disconnects_reaching_the_threshold_ban_the_peer_id", 3,
            Fault(2), Banned(false, "two of three should not ban yet"),
            Fault(), Banned(true, "the third consecutive fault must ban"));
        // A fork-digest mismatch near a BPO rotation is not misbehaviour: it must not count toward,
        // or survive as, a fault streak that a later unrelated fault could otherwise complete.
        yield return BanCase("A_non_fault_disconnect_resets_the_consecutive_fault_streak", 2,
            Fault(), (manager, _) => manager.RecordDisconnect("peerA", 1, 1, GoodbyeReason.IrrelevantNetwork, "fork digest mismatch"),
            Fault(), Banned(false, "the reset streak is only one fault long, not two"));
        // A slow peer is not a hostile one: banning it starves the pool while few peers are usable.
        yield return BanCase("Drops_for_silence_alone_never_ban_a_peer_that_answered_before", 3,
            Fault(10, "repeated failures, last: request timed out", unresponsive: true), (manager, _) =>
            {
                using IDisposable assertionScope = Assert.EnterMultipleScope();
                Assert.That(manager.IsBannedForTest("peerA"), Is.False);
                Assert.That(manager.GetPeerDiagnostics().Single(d => d.PeerId == "peerA").DisconnectCount, Is.EqualTo(10), "the drops stay in the diagnostics");
            });
        yield return BanCase("A_drop_for_silence_does_not_excuse_the_violations_around_it", 3,
            Fault(detail: "invalid response"), Fault(detail: "repeated failures, last: request timed out", unresponsive: true),
            Fault(detail: "invalid response"), Banned(false, "two violations are below the threshold"),
            Fault(detail: "invalid response"), Banned(true, "three violations ban the peer however many timeouts sit between them"));
        yield return BanCase("A_ban_ends_after_the_configured_time_and_the_peer_then_starts_from_a_clean_streak", 3,
            Fault(3, "invalid response"), Banned(true, "test setup: three violations ban the peer"),
            Advance(banTime - TimeSpan.FromSeconds(1)), Banned(true, "the ban holds until its time is up"), Advance(TimeSpan.FromSeconds(1)),
            (manager, _) => Assert.That(manager.GetPeerDiagnostics().Single(d => d.PeerId == "peerA").Banned, Is.False, "the diagnostics must not report a ban that has run out"),
            Banned(false, "the ban must end once its time is up"), Fault(detail: "invalid response"),
            Banned(false, "the old streak must not carry over: one violation after the ban is not three"));
        yield return BanCase("A_fault_during_a_ban_does_not_extend_it", 1,
            Fault(detail: "invalid response", messages: 0, failures: 0), Advance(banTime / 2),
            Fault(detail: "invalid response", messages: 0, failures: 0), Advance(banTime / 2),
            Banned(false, "the ban ends when the first ban's time is up, a later fault must not restart it"));
    }

    private static TestCaseData BanCase(string name, int threshold, params Action<PeerManager, ManualTimestamper>[] stages) =>
        new TestCaseData(threshold, stages).SetName(name);

    private static Action<PeerManager, ManualTimestamper> Fault(int count = 1, string detail = "repeated failures", int messages = 1, int failures = 1, bool unresponsive = false) =>
        (manager, _) =>
        {
            for (int i = 0; i < count; i++)
            {
                manager.RecordDisconnect("peerA", messages, failures, GoodbyeReason.Fault, detail, unresponsive);
            }
        };

    private static Action<PeerManager, ManualTimestamper> Banned(bool expected, string? message = null) =>
        (manager, _) => Assert.That(manager.IsBannedForTest("peerA"), Is.EqualTo(expected), message);

    private static Action<PeerManager, ManualTimestamper> Advance(TimeSpan elapsed) => (_, time) => time.Add(elapsed);

    [Test]
    public void Diagnostics_report_the_ban_and_disconnect_history_of_a_peer_that_is_not_connected()
    {
        PeerManager manager = NewManagerWithoutSessions(faultDisconnectsBeforeBan: 1);

        manager.RecordDisconnect("peerA", messagesSent: 7, failuresReported: 2, GoodbyeReason.Fault, "repeated failures");

        PeerManager.PeerDiagnostics diagnostics = manager.GetPeerDiagnostics().Single(d => d.PeerId == "peerA");
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(diagnostics.Connected, Is.False);
        Assert.That(diagnostics.Banned, Is.True);
        Assert.That(diagnostics.DisconnectCount, Is.EqualTo(1));
        Assert.That(diagnostics.MessagesSent, Is.EqualTo(7));
        Assert.That(diagnostics.FailuresReported, Is.EqualTo(2));
        Assert.That(diagnostics.LastDisconnectReason, Is.EqualTo("Fault"));
    }

    public enum CeilingAdmission { SequentialDials, ConcurrentDials, StaticReconnect, InboundSession, InboundWithFaultHistory }

    [TestCase(CeilingAdmission.SequentialDials, 2)]
    [TestCase(CeilingAdmission.ConcurrentDials, 3)]
    [TestCase(CeilingAdmission.StaticReconnect, 2)]
    [TestCase(CeilingAdmission.InboundSession, 2)]
    [TestCase(CeilingAdmission.InboundWithFaultHistory, 2)]
    [CancelAfter(60_000)]
    public async Task Admission_paths_enforce_the_peer_band_ceiling(CeilingAdmission path, int remoteCount, CancellationToken token)
    {
        Node[] remotes = [.. Enumerable.Range(0, remoteCount).Select(_ => CreateNode())];
        Node local = CreateNode();
        SetMatchingStatus([.. remotes, local]);
        local.Config.MaxPeerCount = 1;
        if (path == CeilingAdmission.StaticReconnect) local.Config.TargetPeerCount = 1;
        if (path == CeilingAdmission.InboundWithFaultHistory) local.Config.FaultDisconnectsBeforeBan = 2;
        await using PeerHostScope nodes = new([local.P2P, .. remotes.Select(static node => node.P2P)]);
        await nodes.StartAsync(token, [.. remotes.Select(static node => node.P2P), local.P2P]);
        PeerManager manager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance);

        switch (path)
        {
            case CeilingAdmission.SequentialDials:
                Assert.That(await manager.TryAddPeerAsync(LoopbackAddress(remotes[0].P2P), token), Is.True);
                Assert.That(manager.PeerCount, Is.EqualTo(1));
                Assert.That(await manager.TryAddPeerAsync(LoopbackAddress(remotes[1].P2P), token), Is.False,
                    "already at MaxPeerCount: must refuse without attempting the second dial");
                break;
            case CeilingAdmission.ConcurrentDials:
                // Every admission races before the others inserted into the pool: reserve capacity before dialing.
                bool[] admitted = await Task.WhenAll(remotes.Select(remote => manager.TryAddPeerAsync(LoopbackAddress(remote.P2P), token)));
                Assert.That(admitted.Count(static accepted => accepted), Is.EqualTo(1), "only one concurrent dial may be admitted");
                break;
            case CeilingAdmission.StaticReconnect:
                // Static peers are exempt from trimming, so reconnect itself must reserve capacity.
                local.Config.StaticPeers = string.Join(",", remotes.Select(remote => LoopbackAddress(remote.P2P)));
                await manager.RunMaintenanceRoundAsync(token);
                break;
            default:
                Node knocking = remotes[1];
                string knockingId = knocking.P2P.LocalPeerId!.ToString();
                bool hasHistory = path == CeilingAdmission.InboundWithFaultHistory;
                if (hasHistory) manager.RecordDisconnect(knockingId, 0, 0, GoodbyeReason.Fault, "repeated failures");
                Assert.That(await manager.TryAddPeerAsync(LoopbackAddress(remotes[0].P2P), token), Is.True);
                using (BeaconP2P.SessionWatch sessions = local.P2P.WatchSessions(knocking.P2P.LocalPeerId!))
                {
                    await PeerSessionNodes.DialToBeRefusedAsync(knocking.P2P, local.P2P, token);
                    if (hasHistory)
                    {
                        await WaitUntilAsync(() => manager.GetPeerDiagnostics().Single(d => d.PeerId == knockingId).LastDisconnectReason == "TooManyPeers", token, "the refusal was never recorded");
                        manager.RecordDisconnect(knockingId, 0, 0, GoodbyeReason.Fault, "repeated failures");
                        Assert.That(manager.IsBannedForTest(knockingId), Is.True);
                    }
                    else
                    {
                        // Remote teardown proves refusal completed before checking the absence of a history record.
                        await WaitUntilAsync(() => knocking.P2P.SessionCountForTest == 0, token, "the refused session was not torn down");
                        await WaitUntilAsync(() => local.P2P.SessionCountForTest == 1, token, "the refused session was not torn down");
                        using (Assert.EnterMultipleScope())
                        {
                            Assert.That(sessions.Opened, Is.EqualTo(1), "the knocking session reached this node");
                            Assert.That(manager.GetPeerDiagnostics().Any(d => d.PeerId == knockingId), Is.False,
                                "never-admitted ids must not evict real peers' disconnect history");
                        }
                    }
                }
                break;
        }

        Assert.That(manager.PeerCount, Is.EqualTo(1), "no admission path may exceed MaxPeerCount");
    }

    [TestCase(false, TestName = "Maintenance_round_trims_the_worst_peer_once_over_the_high_watermark")]
    [TestCase(true, TestName = "A_static_peer_that_connected_to_us_first_is_still_exempt_from_trimming")]
    [CancelAfter(60_000)]
    public async Task Maintenance_trims_the_worst_peer_unless_its_inbound_session_is_static(bool inboundStatic, CancellationToken token)
    {
        AdmissionWatch watch = new();
        Node worse = CreateNode();
        Node better = CreateNode();
        Node local = CreateNode(logManager: inboundStatic ? watch.LogManager : null);
        SetMatchingStatus(worse, better, local);
        worse.StatusHolder.CurrentStatus.HeadSlot = AnchorSlot;
        better.StatusHolder.CurrentStatus.HeadSlot = AnchorSlot + 100;
        await using PeerHostScope nodes = new(local.P2P, worse.P2P, better.P2P);
        await nodes.StartAsync(token, worse.P2P, better.P2P, local.P2P);
        if (inboundStatic) local.Config.StaticPeers = LoopbackAddress(worse.P2P);
        PeerManager peerManager = inboundStatic ? watch.Watch(local, worse, better)
            : new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance);
        if (inboundStatic)
        {
            // Its inbound address differs from its configured static address: exemption must use the peer id.
            await PeerSessionNodes.DialAsync(worse.P2P, local.P2P, token);
            await watch.AdmittedAsync("the static peer's inbound session was never admitted", token);
            Assert.That(peerManager.PeerCount, Is.EqualTo(1));
        }
        else Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(worse.P2P), token), Is.True);
        Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(better.P2P), token), Is.True, () => watch.Describe("the second peer was not admitted"));
        if (!inboundStatic) Assert.That(peerManager.PeerCount, Is.EqualTo(2));

        local.Config.MaxPeerCount = 1;
        local.Config.TargetPeerCount = 1;
        await peerManager.RunMaintenanceRoundAsync(token);

        Assert.That(peerManager.PeerCount, Is.EqualTo(1), "must trim down to the target");
        Assert.That(peerManager.GetBestPeers(0).Single().HeadSlot, Is.EqualTo(inboundStatic ? AnchorSlot : AnchorSlot + 100),
            inboundStatic ? "the static peer is the worse one by head slot and must still be the one kept"
                : "the peer with the lower head slot is the worst and should be trimmed");
        if (!inboundStatic)
        {
            PeerManager.PeerDiagnostics trimmed = peerManager.GetPeerDiagnostics().Single(d => d.HeadSlot != AnchorSlot + 100 && d.DisconnectCount > 0);
            Assert.That(trimmed.LastDisconnectReason, Is.EqualTo("TooManyPeers"));
        }
    }

    [TestCase(false, TestName = "A_banned_static_peer_is_not_reconnected_even_though_it_is_reachable")]
    [TestCase(true, TestName = "A_banned_static_peer_is_reconnected_once_its_ban_has_ended")]
    [CancelAfter(60_000)]
    public async Task Static_peer_reconnect_observes_the_ban_and_its_expiry(bool expire, CancellationToken token)
    {
        Node server = CreateNode();
        Node client = CreateNode();
        SetMatchingStatus(server, client);
        await using PeerHostScope nodes = new(client.P2P, server.P2P);
        await nodes.StartAsync(token, server.P2P, client.P2P);

        string address = LoopbackAddress(server.P2P);
        client.Config.StaticPeers = address;
        ManualTimestamper? time = expire ? new() : null;
        PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance, timestamper: time);
        string peerId = PeerManager.ExtractPeerIdForTest(address);
        // The address stays reachable, so connectivity failure cannot explain a refusal.
        int faults = expire ? client.Config.FaultDisconnectsBeforeBan : 3;
        for (int i = 0; i < faults; i++)
        {
            peerManager.RecordDisconnect(peerId, 0, 0, GoodbyeReason.Fault, expire ? "invalid response" : "repeated failures");
        }
        Assert.That(peerManager.IsBannedForTest(peerId), Is.True, "test setup: the faults must have banned it already");

        await peerManager.RunMaintenanceRoundAsync(token);
        Assert.That(peerManager.PeerCount, Is.EqualTo(0), "the static-peer reconnect loop must not dial a banned id");
        if (expire)
        {
            time!.Add(TimeSpan.FromMinutes(client.Config.PeerBanMinutes));
            await peerManager.RunMaintenanceRoundAsync(token);
            Assert.That(peerManager.PeerCount, Is.EqualTo(1), "a peer whose ban has ended is admitted like any other");
        }
    }

    [TestCase(false, TestName = "A_session_the_remote_opened_is_admitted_and_reported_as_inbound_with_its_agent_string")]
    [TestCase(true, TestName = "Peers_surface_reports_peer_id_direction_state_multiaddr_agent_and_enr_for_a_connected_peer")]
    [CancelAfter(60_000)]
    public async Task Connected_peer_records_preserve_identity_direction_session_reuse_and_failure_labels(bool outbound, CancellationToken token)
    {
        AdmissionWatch watch = new();
        Node remote = CreateNode();
        Node local = CreateNode(logManager: outbound ? null : watch.LogManager);
        SetMatchingStatus(remote, local);
        string agent = outbound ? "test-remote/outbound-4.5.6" : "test-remote/inbound-1.2.3";
        remote.P2P.IdentifySettingsForTest.AgentVersion = agent;
        const string discoveredEnr = "enr:-discovered-test-record";
        await using PeerHostScope nodes = new(local.P2P, remote.P2P);
        await nodes.StartAsync(token, remote.P2P, local.P2P);
        string address = LoopbackAddress(remote.P2P);
        string expectedPeerId = outbound ? PeerManager.ExtractPeerIdForTest(address) : remote.P2P.LocalPeerId!.ToString();
        PeerManager peerManager = outbound ? new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance) : watch.Watch(local, remote);
        if (outbound) Assert.That(await peerManager.TryAddPeerAsync(address, token, discoveredEnr), Is.True);
        else
        {
            await PeerSessionNodes.DialAsync(remote.P2P, local.P2P, token);
            await watch.AdmittedAsync("the manager never admitted the session the remote opened", token);
            Assert.That(peerManager.PeerCount, Is.EqualTo(1), () => watch.Describe("the admitted peer does not match"));
        }

        IPeerDirectory directory = peerManager;
        PeerRecord record = directory.Peers.Single();
        PeerDirection direction = outbound ? PeerDirection.Outbound : PeerDirection.Inbound;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.PeerId, Is.EqualTo(expectedPeerId), () => watch.Describe("the admitted peer does not match"));
            Assert.That(record.Direction, Is.EqualTo(direction), () => watch.Describe("the remote admission direction does not match"));
            Assert.That(record.State, Is.EqualTo(PeerConnectionState.Connected), () => watch.Describe("the admitted peer does not match"));
            // Must be the rewritten loopback address, not the pre-connect 0.0.0.0 dial address.
            Assert.That(record.LastKnownMultiaddr, Does.Contain("127.0.0.1"), () => watch.Describe("the admitted peer does not match"));
            Assert.That(record.AgentVersion, Is.EqualTo(agent), () => watch.Describe("the identify agent string the remote advertised, not null and not our own"));
            Assert.That(record.Enr, outbound ? Is.EqualTo(discoveredEnr) : Is.Null, () => watch.Describe("the admitted peer does not match"));
        }

        Assert.That(directory.TryGetPeer(expectedPeerId, out PeerRecord lookedUp), Is.True, () => watch.Describe("the admitted peer does not match"));
        if (outbound)
        {
            Assert.That(lookedUp.PeerId, Is.EqualTo(expectedPeerId));
            Assert.That(lookedUp.Enr, Is.EqualTo(discoveredEnr));

            long before = FailureCount("ProtocolViolation");
            peerManager.GetBestPeers(0).Single().ReportFailure(PeerFailureReason.ProtocolViolation, "some unbounded detail nobody should turn into a label: " + Guid.NewGuid());
            long after = FailureCount("ProtocolViolation");

            Assert.That(after - before, Is.EqualTo(1), "the closed-cardinality reason, not the free-text detail, is the metric label");
        }
        else
        {
            Assert.That(lookedUp.Direction, Is.EqualTo(PeerDirection.Inbound), () => watch.Describe("the admitted peer does not match"));
            // Straight at the libp2p layer: the manager's own dial path short-circuits on "already
            // connected" before it ever dials, so only a direct dial exercises the session reuse.
            ISession reused = await local.P2P.DialPeerAsync(Multiaddress.Decode(LoopbackAddress(remote.P2P)), token);

            Assert.That(local.P2P.TryGetEstablishedSession(remote.P2P.LocalPeerId!, out ISession? established), Is.True, () => watch.Describe("the admitted peer does not match"));
            Assert.That(reused, Is.SameAs(established), () => watch.Describe("the dial must hand back the session the remote opened, not open a second one"));
            Assert.That((await local.P2P.GetSessionInfoAsync(reused, token)).Direction, Is.EqualTo(PeerDirection.Inbound), () => watch.Describe("the admitted peer does not match"));
            Assert.That(local.P2P.SessionCountForTest, Is.EqualTo(1), () => watch.Describe("one connection, not a second outbound one"));

            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(remote.P2P), token), Is.True, () => watch.Describe("already connected counts as success"));

            Assert.That(peerManager.PeerCount, Is.EqualTo(1), () => watch.Describe("one session, one entry"));
            Assert.That(directory.Peers.Single().Direction, Is.EqualTo(PeerDirection.Inbound), () => watch.Describe("the admitted peer does not match"));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_banned_peer_that_connects_to_us_is_refused(CancellationToken token)
    {
        Node banned = CreateNode();
        Node local = CreateNode();
        SetMatchingStatus(banned, local);

        await using PeerHostScope nodes = new(local.P2P, banned.P2P);
        await nodes.StartAsync(token, banned.P2P, local.P2P);
        PeerManager peerManager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance);

        string bannedId = banned.P2P.LocalPeerId!.ToString();
        peerManager.RecordDisconnect(bannedId, 0, 0, GoodbyeReason.Fault, "repeated failures");
        peerManager.RecordDisconnect(bannedId, 0, 0, GoodbyeReason.Fault, "repeated failures");
        peerManager.RecordDisconnect(bannedId, 0, 0, GoodbyeReason.Fault, "repeated failures");
        Assert.That(peerManager.IsBannedForTest(bannedId), Is.True, "test setup: three faults must have banned it already");

        await PeerSessionNodes.DialToBeRefusedAsync(banned.P2P, local.P2P, token);

        await WaitUntilAsync(() => peerManager.GetPeerDiagnostics().Single(d => d.PeerId == bannedId).LastDisconnectReason == "Banned", token, "the refusal was never recorded");
        await WaitUntilAsync(() => local.P2P.SessionCountForTest == 0, token, "the refused session was not torn down");
        Assert.That(peerManager.PeerCount, Is.EqualTo(0), "a ban must hold against a peer that connects to us, not only against our own dials");
    }

    [TestCase(false, TestName = "A_session_the_remote_opened_whose_status_exchange_fails_is_closed_not_left_open")]
    [TestCase(true, TestName = "A_dialed_session_whose_status_exchange_fails_is_closed_not_left_open")]
    [CancelAfter(60_000)]
    public async Task Failed_status_admission_closes_the_session(bool outbound, CancellationToken token)
    {
        ScriptedStatusSource refusing = new(static _ => throw new Eth2ReqRespException("status refused for the test"));
        Node remote = CreateNode(refusing);
        Node local = CreateNode();
        SetMatchingStatus(local);

        await using PeerHostScope nodes = new(local.P2P, remote.P2P);
        await nodes.StartAsync(token, remote.P2P, local.P2P);
        ManualTimestamper? clock = outbound ? new() : null;
        PeerManager peerManager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance, timestamper: clock);

        if (outbound)
        {
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(remote.P2P), token), Is.False);
            Assert.That(refusing.Requests, Is.GreaterThanOrEqualTo(2), "the status exchange never reached the remote");
        }
        else
        {
            await PeerSessionNodes.DialToBeRefusedAsync(remote.P2P, local.P2P, token);
            // Both status versions were refused over the open session, so the admission provably threw
            // after the session existed: a count of zero below cannot be the pre-connect zero.
            await PeerSessionNodes.WaitUntilAsync(() => refusing.Requests >= 2, "the status exchange never reached the remote", token);
        }

        await WaitUntilAsync(() => local.P2P.SessionCountForTest == 0, token, "the session whose status exchange failed was left open and uncounted");
        Assert.That(peerManager.PeerCount, Is.EqualTo(0));
        if (!outbound)
        {
            return;
        }

        int requests = refusing.Requests;
        Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(remote.P2P), token), Is.False);
        Assert.That(refusing.Requests, Is.EqualTo(requests), "a failed endpoint is not immediately dialled again");
        clock!.Add(TimeSpan.FromMinutes(15));
        Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(remote.P2P), token), Is.False);
        Assert.That(refusing.Requests, Is.GreaterThan(requests), "an expired backoff permits another dial");
    }

    private static Task WaitUntilAsync(Func<bool> condition, CancellationToken token, string failure) =>
        PeerSessionNodes.WaitUntilAsync(condition, failure, token, pollDelayMilliseconds: 50);

    [Test]
    [CancelAfter(60_000)]
    public async Task Admission_capacity_wait_returns_below_target_and_blocks_at_target(CancellationToken token)
    {
        Node server = CreateNode();
        Node client = CreateNode();
        SetMatchingStatus(server, client);

        await using PeerHostScope nodes = new(client.P2P, server.P2P);
        PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
        int originalTarget = client.Config.TargetPeerCount;
        client.Config.TargetPeerCount = 5;
        using (CancellationTokenSource cts = new(TimeSpan.FromSeconds(5)))
        {
            await peerManager.WaitForAdmissionCapacityAsync(cts.Token);
            Assert.That(cts.IsCancellationRequested, Is.False, "an empty pool below the target must not wait at all");
        }
        client.Config.TargetPeerCount = originalTarget;
        await nodes.StartAsync(token, server.P2P, client.P2P);
        Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(server.P2P), token), Is.True);
        client.Config.TargetPeerCount = 1;

        using CancellationTokenSource shortLived = new(TimeSpan.FromMilliseconds(300));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, shortLived.Token);

        // CatchAsync, not ThrowsAsync: Task.Delay throws the derived TaskCanceledException, and
        // ThrowsAsync requires an exact type match.
        Assert.CatchAsync<OperationCanceledException>(async () => await peerManager.WaitForAdmissionCapacityAsync(linked.Token),
            "at the target watermark the wait must not return on its own");
    }

    [Test]
    public void Maintenance_runs_more_often_while_under_the_low_watermark()
    {
        Node node = CreateNode();
        node.Config.MinPeerCount = 20;
        PeerManager peerManager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance);

        TimeSpan underPeered = peerManager.NextMaintenanceIntervalForTest;

        node.Config.MinPeerCount = 0;
        TimeSpan atWatermark = peerManager.NextMaintenanceIntervalForTest;

        Assert.That(underPeered, Is.LessThan(atWatermark), "MinPeerCount must actually change behaviour, not just be read into nothing");
    }

    [TestCase("no-such-peer", TestName = "TryGetPeer_refuses_an_unknown_peer_id_rather_than_matching_anything")]
    [TestCase("", TestName = "TryGetPeer_refuses_an_empty_id_even_when_an_unresolved_dialing_address_would_otherwise_match_it")]
    public void TryGetPeer_refuses_unmatched_ids(string peerId)
    {
        Node node = CreateNode();
        PeerManager peerManager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance);
        // A real dial clears this unresolved reservation before the test could observe it.
        if (peerId.Length == 0) peerManager.ReserveDialingForTest("/ip4/1.2.3.4/tcp/9000/p2p/");
        IPeerDirectory directory = peerManager;

        Assert.That(directory.TryGetPeer(peerId, out _), Is.False, peerId.Length == 0
            ? "an empty id must never be treated as a wildcard, even when a raw '' address is technically tracked" : null);
    }

    public enum BanTableScenario { AllBanned, ActiveBan, ExpiredBan, NoBans }

    [TestCase(BanTableScenario.AllBanned, TestName = "The_ban_table_stays_within_its_cap_when_every_entry_is_banned")]
    [TestCase(BanTableScenario.ActiveBan, TestName = "A_full_table_evicts_the_oldest_entry_that_is_not_banned_before_any_active_ban")]
    [TestCase(BanTableScenario.ExpiredBan, TestName = "A_full_table_treats_an_expired_ban_as_an_ordinary_entry")]
    [TestCase(BanTableScenario.NoBans, TestName = "The_ban_diagnostics_table_evicts_the_oldest_non_banned_entry_once_over_capacity")]
    public void A_full_ban_table_evicts_the_oldest_entry_without_an_active_ban(BanTableScenario scenario)
    {
        const int capacity = 8192;
        ManualTimestamper time = new();
        int threshold = scenario switch { BanTableScenario.AllBanned => 1, BanTableScenario.NoBans => 1000, _ => 2 };
        PeerManager manager = NewManagerWithoutSessions(threshold, time);
        string reason = scenario == BanTableScenario.NoBans ? "repeated failures" : "invalid response";
        for (int i = 0; i < capacity; i++)
        {
            manager.RecordDisconnect($"peer-{i}", 0, 0, GoodbyeReason.Fault, reason);
            if (i == 0 && scenario is BanTableScenario.ActiveBan or BanTableScenario.ExpiredBan)
            {
                manager.RecordDisconnect("peer-0", 0, 0, GoodbyeReason.Fault, reason);
                if (scenario == BanTableScenario.ExpiredBan)
                {
                    time.Add(TimeSpan.FromMinutes(new BeaconChainConfig().PeerBanMinutes));
                }
            }
            if (scenario == BanTableScenario.AllBanned) time.Add(TimeSpan.FromMilliseconds(1));
        }

        Assert.That(manager.GetPeerDiagnostics().Count, Is.EqualTo(capacity));
        manager.RecordDisconnect("peer-new", 0, 0, GoodbyeReason.Fault, reason);

        HashSet<string> remaining = [.. manager.GetPeerDiagnostics().Select(d => d.PeerId)];
        bool firstBanIsActive = scenario == BanTableScenario.ActiveBan;
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(remaining.Count, Is.EqualTo(capacity), scenario == BanTableScenario.AllBanned ? "a table of active bans must not grow past the cap" : null);
        Assert.That(remaining.Contains("peer-0"), Is.EqualTo(firstBanIsActive), scenario switch
        {
            BanTableScenario.AllBanned => "the ban closest to running out is the one that goes",
            BanTableScenario.ExpiredBan => "a ban that has run out protects nothing, and this entry is the oldest",
            BanTableScenario.NoBans => "the oldest tracked non-banned id must be the one evicted",
            _ => "an active ban protects the oldest entry from eviction",
        });
        Assert.That(remaining.Contains("peer-1"), Is.EqualTo(!firstBanIsActive), scenario switch
        {
            BanTableScenario.ActiveBan => "the oldest entry that is not banned goes instead",
            BanTableScenario.NoBans => "only the oldest entry is evicted, not an arbitrary one",
            _ => null,
        });
        Assert.That(remaining.Contains("peer-new"), Is.True);
        if (firstBanIsActive) Assert.That(manager.IsBannedForTest("peer-0"), Is.True, "a flood of new ids must not push an active ban out of the table");
    }

    [TestCase(int.MinValue, false)]
    [TestCase(-1, false)]
    [TestCase(0, false)]
    [TestCase(int.MaxValue, true)]
    public void An_out_of_range_ban_duration_does_not_throw(int banMinutes, bool expectedBanned)
    {
        Node node = CreateNode();
        node.Config.FaultDisconnectsBeforeBan = 1;
        node.Config.PeerBanMinutes = banMinutes;
        PeerManager manager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance);

        Assert.DoesNotThrow(() => manager.RecordDisconnect("peerA", 0, 0, GoodbyeReason.Fault, "invalid response"));
        Assert.That(manager.IsBannedForTest("peerA"), Is.EqualTo(expectedBanned));
    }

    private static long FailureCount(string reason) =>
        Metrics.BeaconChainPeerFailuresByReason.TryGetValue(new StringLabel(reason), out long count) ? count : 0;

    private static PeerManager NewManagerWithoutSessions(int faultDisconnectsBeforeBan, ITimestamper? timestamper = null)
    {
        Node node = CreateNode();
        node.Config.FaultDisconnectsBeforeBan = faultDisconnectsBeforeBan;
        return new PeerManager(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance, timestamper: timestamper);
    }

    internal static void SetMatchingStatus(params Node[] nodes)
    {
        byte[] forkDigest = ForkDigest.Compute(Spec, Spec.GetEpoch(Spec.GetSlotAtTime((ulong)Timestamper.Default.UnixTime.Seconds)));
        foreach (Node node in nodes)
        {
            node.StatusHolder.CurrentStatus = new StatusMessageV2
            {
                ForkDigest = forkDigest,
                FinalizedRoot = Hash256.Zero,
                FinalizedEpoch = Spec.GetEpoch(AnchorSlot),
                HeadRoot = Hash256.Zero,
                HeadSlot = AnchorSlot,
                EarliestAvailableSlot = AnchorSlot,
            };
        }
    }

    internal static string LoopbackAddress(BeaconP2P node)
    {
        string address = node.ListenAddresses.First().ToString().Replace("0.0.0.0", "127.0.0.1");
        if (!address.Contains("/p2p/"))
        {
            address += $"/p2p/{node.LocalPeerId}";
        }

        return address;
    }

    internal record Node(BeaconP2P P2P, BeaconChainStatusHolder StatusHolder, BeaconChainConfig Config, BeaconChainStore Store, LocalMetadataSource Metadata);

    internal static Node CreateNode(IBeaconChainStatusSource? statusSource = null, ILogManager? logManager = null, TimeSpan? requestTimeout = null)
    {
        BeaconChainConfig config = new() { P2PPort = 0 };
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        BeaconChainStatusHolder statusHolder = new(Spec, Timestamper.Default);
        LocalMetadataSource metadataSource = new();
        ILogManager hostLogs = logManager ?? LoopbackTrace.NewNode() ?? LimboLogs.Instance;
        BeaconP2P p2p = requestTimeout is { } timeout
            ? new BeaconP2P(config, Spec, store, statusSource ?? statusHolder, metadataSource, new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), hostLogs) { RequestTimeout = timeout }
            : new BeaconP2P(config, Spec, store, statusSource ?? statusHolder, metadataSource, new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), hostLogs);
        return new Node(p2p, statusHolder, config, store, metadataSource);
    }

    private sealed class AdmissionWatch
    {
        private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(30);

        private readonly List<string> _lines = [];
        private readonly TaskCompletionSource _admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Node[] _nodes = [];

        public AdmissionWatch() => LogManager = new OneLoggerLogManager(new ILogger(new TestLogRecorder(TestLogLevels.All & ~TestLogLevels.Trace,
            (level, text, ex) => Add(level == LogLevel.Error && ex is not null ? $"{text}: {ex}" : text))));

        public ILogManager LogManager { get; }

        public PeerManager Watch(Node local, params Node[] others)
        {
            _nodes = [local, .. others];
            PeerManager manager = new(local.P2P, local.Config, local.StatusHolder, LogManager);
            manager.PeerAdmitted += _ => _admitted.TrySetResult();
            return manager;
        }

        public async Task AdmittedAsync(string failure, CancellationToken token)
        {
            try
            {
                await _admitted.Task.WaitAsync(HangBound, token);
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            {
                Assert.Fail(Describe(failure));
            }
        }

        public string Describe(string failure)
        {
            string state = string.Join("; ", _nodes.Select(static (n, i) => $"node {i}: sessions={n.P2P.SessionCountForTest} identifyTimeouts={n.P2P.IdentifyTimeoutsForTest}"));
            string[] lines;
            lock (_lines)
            {
                lines = [.. _lines];
            }

            return $"{failure} ({state}). Log:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
        }

        private void Add(string text)
        {
            lock (_lines)
            {
                _lines.Add(text);
            }
        }
    }
}
