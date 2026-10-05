// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Reflection;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NSubstitute;
using static Nethermind.BeaconChain.Test.P2P.PeerSessionNodes;

namespace Nethermind.BeaconChain.Test.P2P;

public class PeerFailureLimitFallbackTests
{
    private const int Limit = 8;
    private const string AddressPrefix = "/ip4/10.0.0.1/tcp/9000/p2p/16Uiu2HAm";

    [Test]
    public async Task Every_peer_at_the_limit_leaves_the_two_least_failed_offered_but_never_one_that_sent_invalid_data([Values] bool staleStatus, [Values] bool violatorPassedHealthCheck)
    {
        Node node = Create();
        await using PeerHostScope hosts = new(node.P2P);
        ManualTimestamper clock = new();
        PeerManager manager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance, timestamper: clock);
        // Four violations reach the limit with the fewest consecutive failures, and none once a health check passes.
        IBeaconSyncPeer violator = AddPeer(manager, "Violator", PeerFailureReason.ProtocolViolation, Limit / 2);
        IBeaconSyncPeer least = AddPeer(manager, "Least", PeerFailureReason.RequestFailed, Limit);
        IBeaconSyncPeer next = AddPeer(manager, "Next", PeerFailureReason.RequestFailed, Limit + 1);
        IBeaconSyncPeer most = AddPeer(manager, "Most", PeerFailureReason.RequestFailed, Limit + 2);
        if (violatorPassedHealthCheck)
        {
            PassHealthCheck(violator);
        }

        ulong slot = Status.HeadSlot;
        if (staleStatus)
        {
            slot++;
            clock.Add(TimeSpan.FromSeconds(1));
            manager.MinStatusRefreshInterval = TimeSpan.MaxValue;
            ((IBeaconSyncPeerPool)manager).RefreshStatusesBehind(slot + 10, "the test moved the chain past the peers");
            foreach (IBeaconSyncPeer peer in new[] { violator, least, next, most })
            {
                await (Task)typeof(PeerManager).GetMethod("RefreshStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, [peer, CancellationToken.None])!;
            }
        }

        Assert.That(manager.GetBestPeers(slot).Select(static p => p.Id), Is.EquivalentTo(new[] { least.Id, next.Id }));
    }

    // A passing health check clears the run's violation flag, so only the request-failure marker keeps the violator out here.
    [Test]
    public async Task A_violator_is_offered_at_the_limit_again_only_once_all_its_failures_were_forgiven([Values] bool served, [Values] bool allForgiven)
    {
        Node node = Create();
        await using PeerHostScope hosts = new(node.P2P);
        ManualTimestamper clock = new();
        PeerManager manager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance, timestamper: clock);
        ISession answering = Substitute.For<ISession>();
        answering.DialAsync<BeaconBlocksByRootProtocolV2, Hash256[], IReadOnlyList<ForkedSignedBeaconBlock>>(default!, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([]));
        IBeaconSyncPeer violator = AddPeer(manager, "Violator", PeerFailureReason.ProtocolViolation, Limit / 2, session: answering);
        PassHealthCheck(violator);
        int forgiven = allForgiven ? Limit : 1;
        if (served)
        {
            for (int i = 0; i < forgiven; i++) await violator.RequestBlocksByRootAsync([Hash256.Zero], default);
        }
        else
        {
            clock.Add(PeerManager.RequestFailureDecayInterval * forgiven);
        }

        for (int i = 0; i < forgiven; i++) violator.ReportFailure(PeerFailureReason.RequestFailed);
        IBeaconSyncPeer other = AddPeer(manager, "Other", PeerFailureReason.RequestFailed, Limit + 3);

        Assert.That(manager.GetBestPeers(0), allForgiven ? Is.EquivalentTo(new[] { violator, other }) : Is.EqualTo(new[] { other }));
    }

    public enum Unoffered
    {
        HealthCheckReplyFailedACheck,
        SessionClosed,
    }

    // Each peer left out here has fewer consecutive failures than the one offered, so only the exclusion keeps it out.
    [Test]
    [CancelAfter(30_000)]
    public async Task Every_peer_at_the_limit_still_leaves_out_one_whose_health_check_reply_failed_a_check_or_whose_session_closed([Values] Unoffered reason, CancellationToken token)
    {
        Node node = Create();
        await using PeerHostScope hosts = new(node.P2P);
        await node.P2P.StartAsync(token);
        PeerManager manager = node.CreatePeerManager();
        IBeaconSyncPeer offered = AddPeer(manager, "Offered", PeerFailureReason.RequestFailed, Limit + 3);
        if (reason == Unoffered.SessionClosed)
        {
            AddPeer(manager, "Closed", PeerFailureReason.RequestFailed, Limit, session: new LocalPeer.Session(node.P2P.LocalPeerForTest!));
        }
        else
        {
            IBeaconSyncPeer violator = AddPeer(manager, "Violator", PeerFailureReason.RequestFailed, 0);
            for (int i = 0; i < Limit; i++) await manager.HandleHealthFailureAsync(violator, new TimeoutException(), long.MaxValue, default);
            await manager.HandleHealthFailureAsync(violator, new InvalidOperationException("reply failed a check"), long.MaxValue, default);
            Assert.That(PeerManager.ConsecutiveFailuresForTest(violator), Is.EqualTo(1), "test setup: the violator is the least-failed");
        }

        Assert.That(manager.GetBestPeers(0), Is.EqualTo(new[] { offered }));
    }

    [Test]
    public async Task Among_peers_tied_at_the_limit_the_two_first_in_selection_order_are_offered()
    {
        Node node = Create();
        await using PeerHostScope hosts = new(node.P2P);
        PeerManager manager = node.CreatePeerManager();
        IBeaconSyncPeer[] peers = [.. Enumerable.Range(0, 20).Select(i => AddPeer(manager, $"Tied{i}", PeerFailureReason.RequestFailed, Limit, WithHead(Status.HeadSlot + (ulong)i)))];

        Assert.That(manager.GetBestPeers(0), Is.EqualTo(new[] { peers[19], peers[18] }));
    }

    [Test]
    public async Task A_peer_at_the_limit_is_left_out_while_another_peer_is_under_it()
    {
        Node node = Create();
        await using PeerHostScope hosts = new(node.P2P);
        PeerManager manager = node.CreatePeerManager();
        AddPeer(manager, "AtLimit", PeerFailureReason.RequestFailed, Limit);
        IBeaconSyncPeer under = AddPeer(manager, "Under", PeerFailureReason.RequestFailed, Limit - 1);

        Assert.That(manager.GetBestPeers(0), Is.EqualTo(new[] { under }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Range_sync_advances_when_every_peer_is_at_the_limit_and_never_asks_the_one_that_sent_invalid_data(CancellationToken token)
    {
        const ulong anchorSlot = 10;
        const ulong targetSlot = 14;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(anchorSlot, 11, 12, 13, 14);
        Node node = Create();
        await using PeerHostScope hosts = new(node.P2P);
        PeerManager manager = node.CreatePeerManager();
        Dictionary<string, RangeSyncTests.StubPeer> served = [];
        foreach ((string name, PeerFailureReason reason, int failures) in new[] { ("Violator", PeerFailureReason.ProtocolViolation, Limit / 2), ("First", PeerFailureReason.RequestFailed, Limit), ("Second", PeerFailureReason.RequestFailed, Limit) })
        {
            IBeaconSyncPeer peer = AddPeer(manager, name, reason, failures, WithHead(targetSlot));
            served[peer.Id] = new RangeSyncTests.StubPeer(peer.Id, targetSlot,
                (startSlot, count) => [.. chain.Where(b => b.Message!.Slot >= startSlot && b.Message.Slot < startSlot + count).Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))]);
        }

        Assert.That(manager.GetBestPeers(anchorSlot + 1), Has.Count.EqualTo(2), "test setup: every peer is at the limit");
        RangeSync sync = new(new SelectedByManager(manager, served), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, RangeSyncTests.ClockAtGenesis(BeaconChainSpec.Mainnet));

        List<ulong> imported = [];
        await foreach (ForkedSignedBeaconBlock block in sync.Run(anchorRoot, anchorSlot, () => targetSlot, token))
        {
            imported.Add(block.Slot);
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(imported, Is.EqualTo(chain.Select(static b => b.Message!.Slot)));
        Assert.That(served[AddressPrefix + "Violator"].Requests, Is.Zero);
    }

    private static IBeaconSyncPeer AddPeer(PeerManager manager, string name, PeerFailureReason reason, int failures, StatusMessageV2? status = null, ISession? session = null)
    {
        if (session is null)
        {
            session = Substitute.For<ISession>();
            session.DialAsync<StatusProtocolV2, StatusMessageV2, StatusMessageV2>(default!, default)
                .ReturnsForAnyArgs(Task.FromException<StatusMessageV2>(new IOException("status refused for the test")));
        }

        IBeaconSyncPeer peer = manager.AddPeerForTest(session, AddressPrefix + name, status ?? Status);
        for (int i = 0; i < failures; i++)
        {
            peer.ReportFailure(reason);
        }

        return peer;
    }

    private static void PassHealthCheck(IBeaconSyncPeer peer) =>
        typeof(PeerManager).GetNestedType("ManagedPeer", BindingFlags.NonPublic)!.GetMethod("ResetHealthCheckFailures")!.Invoke(peer, null);

    private static StatusMessageV2 WithHead(ulong headSlot)
    {
        StatusMessageV2 status = Status;
        status.HeadSlot = headSlot;
        status.EarliestAvailableSlot = 0;
        return status;
    }

    private sealed class SelectedByManager(PeerManager manager, IReadOnlyDictionary<string, RangeSyncTests.StubPeer> served) : IBeaconSyncPeerPool
    {
        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot) => [.. manager.GetBestPeers(minHeadSlot).Select(p => served[p.Id])];
    }
}
