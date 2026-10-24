// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.LightClient.Consensus;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NSubstitute;

namespace Nethermind.LightClient.Test;

public class BeaconPeerTransportTests
{
    [Test]
    public async Task Native_beacon_requests_use_the_transport_peer_admission()
    {
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        await using BeaconPeerTransport transport = new(ConsensusTests.Spec, LimboLogs.Instance, db, quicPort: 0);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        BeaconP2P host = (BeaconP2P)typeof(BeaconPeerTransport).GetField("_p2p", flags)!.GetValue(transport)!;
        PeerManager manager = (PeerManager)typeof(BeaconPeerTransport).GetField("_peers", flags)!.GetValue(transport)!;
        IServiceProvider services = (IServiceProvider)typeof(BeaconP2P).GetField("_serviceProvider", flags)!.GetValue(host)!;
        BeaconBlocksByRootProtocolV2 blocks = services.GetRequiredService<BeaconBlocksByRootProtocolV2>();
        Func<PeerId, bool> admitted = (Func<PeerId, bool>)typeof(ReqRespProtocolBase).GetProperty("IsAdmitted", flags)!.GetValue(blocks)!;
        PeerId remote = new Identity().PeerId;
        string address = $"/ip4/127.0.0.1/tcp/9000/p2p/{remote}";

        Assert.That(admitted(remote), Is.False, "an unknown peer must not get access to data requests");
        typeof(PeerManager).GetMethod("ReserveDialingForTest", flags)!.Invoke(manager, [address, null]);
        Assert.That(admitted(remote), Is.False, "a pending admission still needs a valid status exchange");

        StatusProtocolV2 status = services.GetRequiredService<StatusProtocolV2>();
        Action<PeerId, StatusMessageV2> served = (Action<PeerId, StatusMessageV2>)typeof(SingleChunkProtocol<StatusMessageV2, StatusMessageV2>)
            .GetProperty("RequestServed", flags)!.GetValue(status)!;
        IBeaconChainStatusSource statusSource = (IBeaconChainStatusSource)typeof(BeaconP2P).GetField("_statusSource", flags)!.GetValue(host)!;
        served(remote, statusSource.CurrentStatus);
        Assert.That(admitted(remote), Is.True, "the status listener must notify this transport's peer manager");

        typeof(PeerManager).GetMethod("ReleaseReservationForTest", flags)!.Invoke(manager, [address]);
        Assert.That(admitted(remote), Is.False, "ending an unsuccessful admission must revoke data access");
    }

    [Test]
    public void Local_persistence_failure_propagates_without_penalizing_the_peer()
    {
        BootstrapPeer peer = new(ConsensusTests.Bootstrap(1, 1));
        Assert.That(async () => await new BeaconPeerTransport.PeerRequests().RequestAsync<string>([peer],
            static (_, _) => Task.FromResult("valid"), static (_, _) => Task.FromException<bool>(new IOException("Disk write failed.")),
            CancellationToken.None), Throws.TypeOf<IOException>().With.Message.EqualTo("Disk write failed."));
        Assert.That(peer.Failures, Is.Empty);
    }

    [Test]
    public async Task Invalid_consensus_data_penalizes_its_peer_and_tries_the_next_one()
    {
        LightClientBootstrap valid = ConsensusTests.Bootstrap(1, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(valid.Header!.Beacon!);
        LightClientBootstrap invalid = ConsensusTests.Bootstrap(1, 1);
        invalid.CurrentSyncCommitteeBranch![0] = Keccak.Compute("invalid committee proof");
        BootstrapPeer bad = new(invalid);
        BootstrapPeer good = new(valid);

        (LightClientBootstrap? response, _) = await new BeaconPeerTransport.PeerRequests().RequestAsync<LightClientBootstrap>(
            [bad, good], (peer, token) => peer.RequestLightClientBootstrapAsync(checkpoint, token),
            Validate<LightClientBootstrap>(bootstrap => _ = new LightClientStore(ConsensusTests.Spec, checkpoint, bootstrap, 1)), CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(response, Is.SameAs(valid));
        Assert.That(bad.Failures, Is.EqualTo([PeerFailureReason.ProtocolViolation]));
        Assert.That(good.Failures, Is.Empty);
    }

    [Test]
    public async Task Irrelevant_signed_update_tries_another_peer_without_penalty()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        BootstrapPeer stale = new(bootstrap, "stale");
        BootstrapPeer current = new(bootstrap, "current");

        (string? selected, _) = await new BeaconPeerTransport.PeerRequests().RequestAsync<string>([stale, current],
            (peer, _) => Task.FromResult(peer.Id), Validate<string>(id =>
            {
                if (id == "stale") throw new IrrelevantLightClientUpdateException();
            }), CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(selected, Is.EqualTo("current"));
        Assert.That(stale.Failures, Is.Empty);
        Assert.That(current.Failures, Is.Empty);
    }

    [Test]
    public async Task Poll_with_only_locally_inapplicable_updates_completes_without_penalizing_peers([Values] bool irrelevant)
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        BootstrapPeer first = new(bootstrap, "first");
        BootstrapPeer second = new(bootstrap, "second");

        string? response = await new BeaconPeerTransport.PeerRequests().PollAsync<string>(() => [first, second],
            (peer, _) => Task.FromResult(peer.Id), Validate<string>(_ =>
            {
                if (irrelevant) throw new IrrelevantLightClientUpdateException();
                throw new LightClientLocalStateException("Committee unavailable.");
            }), CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(response, Is.Null);
        Assert.That(first.Failures, Is.Empty);
        Assert.That(second.Failures, Is.Empty);
    }

    [Test]
    public async Task Optimistic_poll_continues_until_a_peer_advances_the_selected_head(
        [Values] bool invalidFirst, [Values] bool advancingSecond)
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        LightClientStore store = OptimisticStore(bootstrap);
        BootstrapPeer first = new(bootstrap, "first");
        BootstrapPeer second = new(bootstrap, "second");
        LightClientOptimisticUpdate duplicate = Optimistic(10, participants: 512, key: invalidFirst ? (byte)2 : (byte)1);
        LightClientOptimisticUpdate later = Optimistic(advancingSecond ? 11UL : 10UL);
        List<string> requested = [];
        List<ulong> processed = [];

        LightClientOptimisticUpdate? selected = await new BeaconPeerTransport.PeerRequests().PollAsync<LightClientOptimisticUpdate>(
            () => [first, second], (peer, _) =>
            {
                requested.Add(peer.Id);
                return Task.FromResult(peer == first ? duplicate : later);
            }, (update, _) =>
            {
                ulong before = store.OptimisticHeader.Beacon!.Slot;
                store.Process(update, 12);
                processed.Add(update.AttestedHeader!.Beacon!.Slot);
                return Task.FromResult(store.OptimisticHeader.Beacon!.Slot > before);
            }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requested, Is.EqualTo(new[] { "first", "second" }));
            Assert.That(processed, Is.EqualTo(invalidFirst ? new[] { later.AttestedHeader!.Beacon!.Slot }
                : new[] { duplicate.AttestedHeader!.Beacon!.Slot, later.AttestedHeader!.Beacon!.Slot }));
            Assert.That(selected, Is.SameAs(advancingSecond ? later : null));
            Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(advancingSecond ? 11UL : 10UL));
            Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(1UL));
            Assert.That(store.CurrentMaxParticipants, Is.EqualTo(invalidFirst ? 342 : 512));
            Assert.That(first.Failures, Is.EqualTo(invalidFirst ? new[] { PeerFailureReason.ProtocolViolation } : []));
            Assert.That(second.Failures, Is.Empty);
        }
    }

    [Test]
    public async Task Valid_finality_without_finalized_head_advancement_remains_acceptable()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        LightClientStore store = OptimisticStore(bootstrap);
        LightClientUpdate signed = ConsensusTests.Update(1, 10, 11, 1, participants: 512);
        LightClientFinalityUpdate update = new()
        {
            AttestedHeader = signed.AttestedHeader,
            FinalizedHeader = signed.FinalizedHeader,
            FinalityBranch = signed.FinalityBranch,
            SyncAggregate = signed.SyncAggregate,
            SignatureSlot = signed.SignatureSlot,
        };
        BootstrapPeer first = new(bootstrap, "first");
        BootstrapPeer second = new(bootstrap, "second");
        List<string> requested = [];

        Task<LightClientFinalityUpdate> Request(IBeaconSyncPeer peer, CancellationToken _)
        {
            requested.Add(peer.Id);
            return Task.FromResult(update);
        }

        BeaconPeerTransport.PeerRequests requests = new();
        LightClientFinalityUpdate? selected = await requests.PollAsync(
            () => [first, second], Request, Validate<LightClientFinalityUpdate>(value => store.Process(value, 12)), CancellationToken.None);

        LightClientUpdate? retained = store.BestUpdate;
        Assert.That(retained?.FinalizedHeader?.Beacon, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected, Is.SameAs(update));
            Assert.That(requested, Is.EqualTo(new[] { "first" }));
            Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(1UL));
            Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(10UL));
            Assert.That(store.CurrentMaxParticipants, Is.EqualTo(512));
            Assert.That(retained!.FinalizedHeader!.Beacon!.Slot, Is.EqualTo(1UL));
            Assert.That(first.Failures, Is.Empty);
            Assert.That(second.Failures, Is.Empty);
        }

        selected = await requests.PollAsync(() => [first, second], Request,
            Validate<LightClientFinalityUpdate>(value => store.Process(value, 12)), CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected, Is.SameAs(update));
            Assert.That(requested, Is.EqualTo(new[] { "first", "second" }));
            Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(1));
            Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(10));
            Assert.That(first.Failures, Is.Empty);
            Assert.That(second.Failures, Is.Empty);
        }
    }

    [Test]
    public void Cancellation_after_nonadvancing_processing_does_not_query_or_penalize_another_peer([Values] bool lastPeer)
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        LightClientStore store = OptimisticStore(bootstrap);
        LightClientOptimisticUpdate duplicate = Optimistic(10, participants: 512);
        BootstrapPeer first = new(bootstrap, "first");
        BootstrapPeer second = new(bootstrap, "second");
        using CancellationTokenSource caller = new();
        List<string> requested = [];

        Task<LightClientOptimisticUpdate?> poll = new BeaconPeerTransport.PeerRequests().PollAsync<LightClientOptimisticUpdate>(
            () => lastPeer ? [first] : [first, second], (peer, _) =>
            {
                requested.Add(peer.Id);
                return Task.FromResult(duplicate);
            }, (update, _) =>
            {
                ulong before = store.OptimisticHeader.Beacon!.Slot;
                store.Process(update, 12);
                caller.Cancel();
                return Task.FromResult(store.OptimisticHeader.Beacon!.Slot > before);
            }, caller.Token);

        Assert.That(async () => await poll, Throws.InstanceOf<OperationCanceledException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requested, Is.EqualTo(new[] { "first" }));
            Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(10UL));
            Assert.That(store.CurrentMaxParticipants, Is.EqualTo(512));
            Assert.That(first.Failures, Is.Empty);
            Assert.That(second.Failures, Is.Empty);
        }
    }

    [Test]
    public async Task Poll_waits_for_a_reconnecting_peer()
    {
        BootstrapPeer peer = new(ConsensusTests.Bootstrap(1, 1));
        IReadOnlyList<IBeaconSyncPeer> peers = [];
        ManualDeadlineClock clock = new();

        Task<string?> poll = new BeaconPeerTransport.PeerRequests(clock).PollAsync<string>(() => peers,
            (value, _) => Task.FromResult(value.Id), static (_, _) => Task.FromResult(true), CancellationToken.None);

        Assert.That(poll.IsCompleted, Is.False, "an empty peer snapshot must not end the poll before its deadline");
        Assert.That(clock.DueTime, Is.EqualTo(TimeSpan.FromSeconds(1)));
        peers = [peer];
        clock.Expire();
        string? response = await poll;

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(response, Is.EqualTo(peer.Id));
        Assert.That(peer.Failures, Is.Empty);
    }

    [Test]
    public void Poll_without_peers_observes_caller_cancellation([Values] bool alreadyCanceled)
    {
        using CancellationTokenSource caller = new();
        if (alreadyCanceled) caller.Cancel();
        int snapshots = 0;

        Task<string?> poll = new BeaconPeerTransport.PeerRequests(new ManualDeadlineClock()).PollAsync<string>(() =>
        {
            snapshots++;
            return [];
        }, static (_, _) => Task.FromResult("unreachable"), static (_, _) => Task.FromResult(true), caller.Token);
        caller.Cancel();

        Assert.That(async () => await poll, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(snapshots, Is.EqualTo(alreadyCanceled ? 0 : 1));
    }

    [Test]
    public void Poll_with_only_failed_requests_reports_unavailability()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        BootstrapPeer peer = new(bootstrap);

        Assert.That(async () => await new BeaconPeerTransport.PeerRequests().PollAsync<string>(() => [peer],
            (_, _) => Task.FromException<string>(new IOException("Request failed.")), static (_, _) => Task.FromResult(true), CancellationToken.None),
            Throws.TypeOf<IOException>());
        Assert.That(peer.Failures, Is.EqualTo([PeerFailureReason.RequestFailed]));
    }

    [Test]
    public async Task Poll_tries_another_peer_when_one_request_exceeds_its_deadline()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        BootstrapPeer stalled = new(bootstrap, "stalled");
        BootstrapPeer available = new(bootstrap, "available");
        ManualDeadlineClock clock = new();

        Task<string?> poll = new BeaconPeerTransport.PeerRequests(clock).PollAsync<string>(() => [stalled, available], async (peer, token) =>
        {
            if (peer == stalled) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return peer.Id;
        }, static (_, _) => Task.FromResult(true), CancellationToken.None);

        Assert.That(clock.DueTime, Is.EqualTo(TimeSpan.FromSeconds(5)));
        clock.Expire();
        string? response = await poll;

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(response, Is.EqualTo(available.Id));
        Assert.That(stalled.Failures, Is.EqualTo([PeerFailureReason.RequestFailed]));
        Assert.That(available.Failures, Is.Empty);
    }

    [Test]
    public async Task Poll_reaches_a_later_peer_across_overall_deadlines([Values(3, 7, 11)] int slowPeers)
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        LightClientStore store = OptimisticStore(bootstrap);
        BootstrapPeer[] peers = Enumerable.Range(0, slowPeers + 1)
            .Select(index => new BootstrapPeer(bootstrap, index.ToString())).ToArray();
        BootstrapPeer healthy = peers[^1];
        LightClientOptimisticUpdate update = Optimistic(11);
        Channel<IBeaconSyncPeer> started = System.Threading.Channels.Channel.CreateUnbounded<IBeaconSyncPeer>();
        List<string> requested = [];
        LightClientOptimisticUpdate? selected = null;
        ManualDeadlineClock clock = new();
        BeaconPeerTransport.PeerRequests requests = new(clock);

        for (int attempt = 0; attempt < (slowPeers + 2) / 3 + 1 && selected is null; attempt++)
        {
            using CancellationTokenSource overall = new();
            Task<LightClientOptimisticUpdate?> poll = requests.PollAsync(
                () => peers, async (peer, token) =>
                {
                    requested.Add(peer.Id);
                    started.Writer.TryWrite(peer);
                    if (peer != healthy) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return update;
                }, (value, _) => AdvanceOptimistic(store, value), overall.Token);

            for (int request = 0; request < 3; request++)
            {
                IBeaconSyncPeer peer = await started.Reader.ReadAsync();
                if (peer == healthy) break;
                if (request == 2) overall.Cancel();
                else clock.Expire();
            }
            try { selected = await poll; }
            catch (OperationCanceledException) when (overall.IsCancellationRequested) { }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected, Is.SameAs(update), "an overall deadline must not repeatedly restart the same slow prefix");
            Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(11));
            Assert.That(requested, Is.EqualTo(peers.Select(peer => peer.Id)));
            Assert.That(healthy.Failures, Is.Empty);
            for (int i = 0; i < slowPeers; i++)
                Assert.That(peers[i].Failures, Is.EqualTo(i % 3 == 2 ? Array.Empty<PeerFailureReason>() : [PeerFailureReason.RequestFailed]));
        }
    }

    [Test]
    public async Task Poll_keeps_contexts_independent_after_validated_progress([Values] bool advancingFirst)
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        LightClientStore store = OptimisticStore(bootstrap);
        BootstrapPeer first = new(bootstrap, "first");
        BootstrapPeer second = new(bootstrap, "second");
        BeaconPeerTransport.PeerRequests requests = new();
        LightClientOptimisticUpdate initial = Optimistic(11);
        await requests.PollAsync(() => [first, second], (peer, _) => peer == first
            ? Task.FromException<LightClientOptimisticUpdate>(new IOException("Unavailable.")) : Task.FromResult(initial),
            (value, _) => AdvanceOptimistic(store, value), CancellationToken.None);
        List<IBeaconSyncPeer> requested = [];
        LightClientOptimisticUpdate later = Optimistic(12);

        LightClientOptimisticUpdate? selected = await requests.PollAsync(() => [first, second], (peer, _) =>
        {
            requested.Add(peer);
            return Task.FromResult(peer == first && !advancingFirst ? initial : later);
        }, (value, _) => AdvanceOptimistic(store, value), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected, Is.SameAs(later));
            Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(12));
            Assert.That(requested, Is.EqualTo(advancingFirst ? new[] { first } : [first, second]));
            Assert.That(first.Failures, Is.EqualTo([PeerFailureReason.RequestFailed]));
            Assert.That(second.Failures, Is.Empty);
        }

        requested.Clear();
        LightClientOptimisticUpdate next = Optimistic(13);
        BeaconPeerTransport.PeerRequests otherContext = new();
        selected = await otherContext.PollAsync(() => [first, second], (peer, _) =>
        {
            requested.Add(peer);
            return Task.FromResult(next);
        }, (value, _) => AdvanceOptimistic(store, value), CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected, Is.SameAs(next));
            Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(13));
            Assert.That(requested, Is.EqualTo(new[] { first }));
        }
    }

    [Test]
    public async Task Nonadvancing_responses_complete_a_rotation_without_penalty()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        LightClientStore store = OptimisticStore(bootstrap);
        BootstrapPeer first = new(bootstrap, "first");
        BootstrapPeer second = new(bootstrap, "second");
        BeaconPeerTransport.PeerRequests requests = new();
        LightClientOptimisticUpdate duplicate = Optimistic(10);
        LightClientOptimisticUpdate? selected = await requests.PollAsync(() => [first, second], (_, _) => Task.FromResult(duplicate),
            (value, _) => AdvanceOptimistic(store, value), CancellationToken.None);
        Assert.That(selected, Is.Null);
        List<IBeaconSyncPeer> requested = [];
        LightClientOptimisticUpdate later = Optimistic(11);

        selected = await requests.PollAsync(() => [first, second], (peer, _) =>
        {
            requested.Add(peer);
            return Task.FromResult(later);
        }, (value, _) => AdvanceOptimistic(store, value), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected, Is.SameAs(later));
            Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(11));
            Assert.That(requested, Is.EqualTo(new[] { first }));
            Assert.That(first.Failures, Is.Empty);
            Assert.That(second.Failures, Is.Empty);
        }
    }

    [Test]
    public async Task Poll_uses_the_current_snapshot_after_the_last_attempted_peer_is_replaced([Values] bool sameIdentity)
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        LightClientStore store = OptimisticStore(bootstrap);
        BootstrapPeer first = new(bootstrap, "first");
        BootstrapPeer old = new(bootstrap, "old");
        BootstrapPeer replacement = new(bootstrap, sameIdentity ? "old" : "replacement");
        BeaconPeerTransport.PeerRequests requests = new();
        await requests.PollAsync(() => [first, old], (peer, _) => peer == first
            ? Task.FromException<LightClientOptimisticUpdate>(new IOException("Unavailable.")) : Task.FromResult(Optimistic(11)),
            (value, _) => AdvanceOptimistic(store, value), CancellationToken.None);
        List<IBeaconSyncPeer> requested = [];
        LightClientOptimisticUpdate later = Optimistic(12);

        LightClientOptimisticUpdate? selected = await requests.PollAsync(() => [replacement, first], (peer, _) =>
        {
            requested.Add(peer);
            return peer == replacement ? Task.FromResult(later)
                : Task.FromException<LightClientOptimisticUpdate>(new IOException("Unavailable."));
        }, (value, _) => AdvanceOptimistic(store, value), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected, Is.SameAs(later));
            Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(12));
            Assert.That(requested, Is.EqualTo(sameIdentity ? new[] { first, replacement } : [replacement]));
            Assert.That(old.Failures, Is.Empty);
            Assert.That(replacement.Failures, Is.Empty);
        }
    }

    [Test]
    public void Caller_cancellation_does_not_penalize_a_peer()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        BootstrapPeer peer = new(bootstrap);
        using CancellationTokenSource caller = new();

        Task<(string? Response, bool SawInapplicable)> request = new BeaconPeerTransport.PeerRequests(new ManualDeadlineClock()).RequestAsync<string>(
            [peer], async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "unreachable";
            }, static (_, _) => Task.FromResult(true), caller.Token);
        caller.Cancel();

        Assert.That(async () => await request, Throws.TypeOf<TaskCanceledException>());
        Assert.That(peer.Failures, Is.Empty);
    }

    [TestCase(1UL, 2UL, 3UL, 4UL, 3UL)]
    [TestCase(8192UL, 8193UL, 8194UL, 8195UL, 8195UL)]
    [TestCase(16384UL, 16385UL, 16386UL, 16387UL, 16387UL)]
    public async Task Valid_update_inapplicable_to_local_clock_or_committee_does_not_penalize_peer(
        ulong referenceBootstrapSlot, ulong finalizedSlot, ulong attestedSlot, ulong signatureSlot, ulong localSlot)
    {
        LightClientUpdate update = ConsensusTests.Update(finalizedSlot, attestedSlot, signatureSlot, 1);
        LightClientBootstrap referenceBootstrap = ConsensusTests.Bootstrap(referenceBootstrapSlot, 1);
        LightClientStore reference = new(ConsensusTests.Spec,
            SszRoots.HashTreeRoot(referenceBootstrap.Header!.Beacon!), referenceBootstrap, signatureSlot);
        reference.Process(update, signatureSlot);

        LightClientBootstrap localBootstrap = ConsensusTests.Bootstrap(1, 1);
        LightClientStore local = new(ConsensusTests.Spec,
            SszRoots.HashTreeRoot(localBootstrap.Header!.Beacon!), localBootstrap, 1);
        BootstrapPeer peer = new(localBootstrap);

        (LightClientUpdate? response, _) = await new BeaconPeerTransport.PeerRequests().RequestAsync<LightClientUpdate>(
            [peer], (_, _) => Task.FromResult(update), Validate<LightClientUpdate>(value => local.Process(value, localSlot)), CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(reference.FinalizedHeader.Beacon!.Slot, Is.EqualTo(finalizedSlot));
        Assert.That(response, Is.Null);
        Assert.That(peer.Failures, Is.Empty);
        Assert.That(local.FinalizedHeader.Beacon!.Slot, Is.EqualTo(1));
    }

    [TestCase(0UL)]
    [TestCase(8194UL)]
    public async Task Locally_inapplicable_checkpoint_does_not_penalize_peer(ulong currentSlot)
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        BootstrapPeer peer = new(bootstrap);

        (LightClientBootstrap? response, _) = await new BeaconPeerTransport.PeerRequests().RequestAsync<LightClientBootstrap>(
            [peer], (value, token) => value.RequestLightClientBootstrapAsync(checkpoint, token),
            Validate<LightClientBootstrap>(value => _ = new LightClientStore(ConsensusTests.Spec, checkpoint, value, currentSlot)), CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(response, Is.Null);
        Assert.That(peer.Failures, Is.Empty);
    }

    [Test]
    public async Task Bootstrap_supplier_is_preferred_for_optimistic_polling_before_it_has_a_fresh_head()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        BootstrapPeer unknown = new(bootstrap, "unknown");
        BootstrapPeer supplier = new(bootstrap, "supplier");
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock);
        providers.Admit(unknown, PeerDirection.Outbound);
        providers.Admit(supplier, PeerDirection.Outbound);
        BeaconPeerTransport.PeerRequests requests = new(clock, providers: providers);
        await requests.RequestAsync<LightClientBootstrap>([supplier], (peer, token) => peer.RequestLightClientBootstrapAsync(
            SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!), token), Validate<LightClientBootstrap>(value =>
                _ = new LightClientStore(ConsensusTests.Spec, SszRoots.HashTreeRoot(value.Header!.Beacon!), value, 14)), CancellationToken.None);
        List<IBeaconSyncPeer> requested = [];
        LightClientOptimisticUpdate expected = Optimistic(13);

        LightClientOptimisticUpdate? selected = await requests.PollAsync(() => [unknown, supplier], (peer, _) =>
        {
            requested.Add(peer);
            return Task.FromResult(expected);
        }, Validate<LightClientOptimisticUpdate>(value =>
            new LightClientStore(ConsensusTests.Spec, SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!), bootstrap, 14).Process(value, 14)), CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(selected, Is.SameAs(expected));
        Assert.That(requested, Is.EqualTo(new[] { supplier }));
        Assert.That(unknown.Failures, Is.Empty);
    }

    [Test]
    public async Task Validated_fresh_duplicate_retains_supplier_preference_without_advancing_the_store()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        LightClientStore store = OptimisticStore(bootstrap);
        BootstrapPeer unknown = new(bootstrap, "unknown");
        BootstrapPeer supplier = new(bootstrap, "supplier");
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock);
        providers.Admit(unknown, PeerDirection.Outbound);
        providers.Admit(supplier, PeerDirection.Outbound);
        BeaconPeerTransport.PeerRequests requests = new(clock, providers: providers);

        LightClientOptimisticUpdate? selected = await requests.PollAsync(() => [supplier], (_, _) => Task.FromResult(Optimistic(10)),
            (value, _) => AdvanceOptimistic(store, value), CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(selected, Is.Null);
        Assert.That(providers.Preferred<LightClientOptimisticUpdate>([unknown, supplier], 0), Is.EqualTo(1));
        Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(10));
        Assert.That(supplier.Failures, Is.Empty);
    }

    [Test]
    public async Task Unverified_or_old_data_does_not_establish_a_current_supplier([Values] bool invalid)
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        LightClientStore store = OptimisticStore(bootstrap);
        BootstrapPeer supplier = new(bootstrap);
        ManualDeadlineClock clock = new();
        clock.SetSlot(25);
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock);
        providers.Admit(supplier, PeerDirection.Outbound);
        LightClientOptimisticUpdate update = Optimistic(invalid ? 11UL : 10UL);
        if (invalid) update.SyncAggregate!.SyncCommitteeSignature = new BlsSignature(new byte[96]);

        await new BeaconPeerTransport.PeerRequests(clock, providers: providers).RequestAsync([supplier], (_, _) => Task.FromResult(update),
            (value, _) => AdvanceOptimistic(store, value), CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(providers.Preferred<LightClientOptimisticUpdate>([supplier], 0), Is.EqualTo(-1));
        Assert.That(providers.Maintain([]).Reconnect, Is.Null);
        Assert.That(supplier.Failures, Is.EqualTo(invalid ? [PeerFailureReason.ProtocolViolation] : Array.Empty<PeerFailureReason>()));
    }

    [Test]
    public void Supplier_credit_belongs_to_the_current_session()
    {
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock);
        BootstrapPeer old = new(ConsensusTests.Bootstrap(1, 1), "same-identity");
        BootstrapPeer replacement = new(ConsensusTests.Bootstrap(1, 1), old.Id);
        providers.Admit(old, PeerDirection.Outbound);
        providers.RecordValidated(old, Optimistic(13));
        providers.Admit(replacement, PeerDirection.Outbound);
        providers.RecordValidated(old, Optimistic(13));

        Assert.That(providers.Preferred<LightClientOptimisticUpdate>([old, replacement], 0), Is.EqualTo(-1));
        providers.RecordValidated(replacement, Optimistic(13));
        Assert.That(providers.Preferred<LightClientOptimisticUpdate>([old, replacement], 0), Is.EqualTo(1));
    }

    [Test]
    public void Request_failure_clears_only_its_protocol_preference()
    {
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock);
        BootstrapPeer supplier = new(ConsensusTests.Bootstrap(1, 1));
        providers.Admit(supplier, PeerDirection.Outbound);
        providers.RecordValidated(supplier, ConsensusTests.Bootstrap(1, 1));
        providers.RecordValidated(supplier, ConsensusTests.Update(1, 13, 14, 1));
        providers.Failed<LightClientFinalityUpdate>(supplier);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(providers.Preferred<LightClientFinalityUpdate>([supplier], 0), Is.EqualTo(-1));
            Assert.That(providers.Preferred<LightClientOptimisticUpdate>([supplier], 0), Is.Zero);
        }
        providers.RecordValidated(supplier, new LightClientFinalityUpdate { AttestedHeader = Optimistic(13).AttestedHeader });
        Assert.That(providers.Preferred<LightClientFinalityUpdate>([supplier], 0), Is.Zero);
    }

    [Test]
    public async Task Unavailable_optional_data_changes_supplier_selection_without_a_peer_fault(
        [Values] bool wrapped, [Values(ReqRespFraming.ResponseCode.ResourceUnavailable, ReqRespFraming.ResponseCode.InvalidRequest)] byte responseCode)
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        LightClientStore store = OptimisticStore(bootstrap);
        BootstrapPeer unavailable = new(bootstrap, "unavailable");
        BootstrapPeer available = new(bootstrap, "available");
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock);
        providers.Admit(unavailable, PeerDirection.Outbound);
        providers.Admit(available, PeerDirection.Outbound);
        providers.RecordValidated(unavailable, bootstrap);
        Exception failure = new Eth2ReqRespException("Data unavailable.", responseCode);
        if (wrapped) failure = new AggregateException(failure);
        LightClientOptimisticUpdate update = Optimistic(11);
        BeaconPeerTransport.PeerRequests requests = new(clock, providers: providers);
        await requests.RequestAsync<LightClientOptimisticUpdate>([unavailable], (_, _) => Task.FromException<LightClientOptimisticUpdate>(failure),
            (value, _) => AdvanceOptimistic(store, value), CancellationToken.None);
        List<IBeaconSyncPeer> requested = [];

        LightClientOptimisticUpdate? selected = await requests.PollAsync(() => [unavailable, available], (peer, _) =>
            {
                requested.Add(peer);
                return peer == unavailable ? Task.FromException<LightClientOptimisticUpdate>(failure) : Task.FromResult(update);
            },
            (value, _) => AdvanceOptimistic(store, value), CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(selected, Is.SameAs(update));
        Assert.That(requested, Is.EqualTo(new[] { available }), "a failed candidate must not override the next peer rotation");
        Assert.That(providers.Preferred<LightClientOptimisticUpdate>([unavailable], 0), Is.EqualTo(-1));
        Assert.That(unavailable.Failures, Is.EqualTo(responseCode == ReqRespFraming.ResponseCode.ResourceUnavailable
            ? Array.Empty<PeerFailureReason>() : [PeerFailureReason.RequestFailed]));
        Assert.That(available.Failures, Is.Empty);
    }

    [Test]
    public void Successful_supplier_addresses_are_retained_with_a_fixed_capacity()
    {
        ManualDeadlineClock clock = new();
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock);
        BootstrapPeer[] peers = ProviderPeers(17);
        for (int i = 0; i < peers.Length; i++)
        {
            clock.SetSlot((ulong)i + 1);
            providers.Admit(peers[i], PeerDirection.Outbound);
            providers.RecordValidated(peers[i], ConsensusTests.Bootstrap(1, 1));
        }
        List<string> redialed = [];
        for (int i = 0; i < peers.Length; i++)
        {
            string? address = providers.Maintain([]).Reconnect;
            if (address is not null) redialed.Add(address);
        }

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(redialed, Has.Count.EqualTo(16));
        Assert.That(redialed, Does.Not.Contain(peers[0].Id));
        Assert.That(redialed.Distinct().Count(), Is.EqualTo(16));
        Assert.That(providers.Preferred<LightClientBootstrap>([peers[0]], 0), Is.EqualTo(-1));
    }

    [Test]
    public void Admission_churn_evicts_an_unproductive_record_before_a_fresh_supplier()
    {
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock);
        BootstrapPeer[] peers = ProviderPeers(17);
        providers.Admit(peers[0], PeerDirection.Outbound);
        providers.RecordValidated(peers[0], Optimistic(13));

        foreach (BootstrapPeer peer in peers.Skip(1)) providers.Admit(peer, PeerDirection.Outbound);

        Assert.That(providers.Preferred<LightClientOptimisticUpdate>(peers, 0), Is.Zero);
    }

    [Test]
    public void Supplier_search_preserves_fresh_gossip_after_rpc_failure_and_redials_only_outbound_addresses(
        [Values(PeerDirection.Inbound, PeerDirection.Outbound)] PeerDirection supplierDirection)
    {
        ManualDeadlineClock clock = new();
        clock.SetSlot(1);
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock);
        PeerId source = new Identity().PeerId;
        BootstrapPeer[] peers = ProviderPeers(8, source.ToString());
        for (int i = 0; i < peers.Length; i++) providers.Admit(peers[i], i == 0 ? supplierDirection : PeerDirection.Outbound);
        PeerRecord[] connected = Connected(peers);
        connected[0] = connected[0] with { Direction = supplierDirection };
        Assert.That(providers.Maintain(connected), Is.EqualTo(((IBeaconSyncPeer?)null, (string?)null)), "new peers need time to supply data");
        clock.SetSlot(14);
        providers.RecordGossip(source, 13);
        providers.Failed<LightClientOptimisticUpdate>(peers[0]);

        (IBeaconSyncPeer? recycle, _) = providers.Maintain(connected);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recycle, Is.Not.Null.And.Not.SameAs(peers[0]));
            Assert.That(providers.Preferred<LightClientOptimisticUpdate>(peers, 0), Is.EqualTo(-1), "gossip does not prove request support");
        }
        PeerRecord[] disconnected = connected[1..];
        string? redialAddress = supplierDirection == PeerDirection.Outbound ? peers[0].Id : null;
        Assert.That(providers.Maintain(disconnected).Reconnect, Is.EqualTo(redialAddress), "an observed inbound endpoint is not a listening address");
        Assert.That(providers.Maintain(disconnected).Reconnect, Is.Null, "redial goes through a bounded retry cadence");
        clock.SetSlot(17);
        Assert.That(providers.Maintain(disconnected).Reconnect, Is.EqualTo(redialAddress));
        clock.Advance(TimeSpan.FromHours(24));
        Assert.That(providers.Maintain(disconnected).Reconnect, Is.Null, "disconnected addresses expire instead of being retained indefinitely");
    }

    [TestCase(7, false, 0)]
    [TestCase(7, true, 0)]
    [TestCase(7, true, 1)]
    [TestCase(7, true, 2)]
    [TestCase(8, false, 0)]
    [TestCase(8, false, 1)]
    [TestCase(8, false, 2)]
    public void Supplier_search_counts_admission_reservations_and_preserves_fresh_verified_sources(
        int connectedPeers, bool connectingPeer, int freshSuppliers)
    {
        ManualDeadlineClock clock = new();
        clock.SetSlot(1);
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock);
        BootstrapPeer[] peers = ProviderPeers(connectedPeers + (connectingPeer ? 1 : 0));
        BootstrapPeer[] admitted = peers[..connectedPeers];
        foreach (BootstrapPeer peer in admitted) providers.Admit(peer, PeerDirection.Outbound);
        clock.SetSlot(14);
        for (int i = 0; i < admitted.Length; i++) providers.RecordValidated(admitted[i], Optimistic(i < freshSuppliers ? 13UL : 3UL));
        PeerRecord[] occupied = Connected(peers);
        if (connectingPeer) occupied[^1] = occupied[^1] with { State = PeerConnectionState.Connecting };

        (IBeaconSyncPeer? recycle, string? reconnect) = providers.Maintain(occupied);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(reconnect, Is.Null);
        if (occupied.Length < 8 || freshSuppliers == 2) Assert.That(recycle, Is.Null);
        else Assert.That(admitted.Skip(freshSuppliers), Does.Contain(recycle));
    }

    [Test]
    public void Gossip_progress_does_not_expire_request_suppliers_between_healthy_repair_polls()
    {
        ManualDeadlineClock clock = new();
        clock.SetSlot(1);
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock);
        PeerId gossipSource = new Identity().PeerId;
        BootstrapPeer[] peers = ProviderPeers(8, gossipSource.ToString());
        foreach (BootstrapPeer peer in peers) providers.Admit(peer, PeerDirection.Outbound);
        clock.SetSlot(4);
        providers.RecordValidated(peers[1], Optimistic(3));
        providers.RecordValidated(peers[2], Optimistic(3));
        foreach (BootstrapPeer peer in peers.Skip(3)) providers.RecordValidated(peer, ConsensusTests.Bootstrap(1, 1));
        clock.SetSlot(9);
        providers.RecordGossip(gossipSource, 8);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(providers.Maintain(Connected(peers)).Recycle, Is.Null,
                "request suppliers remain useful while gossip delays the next healthy repair poll by sixty seconds");
            Assert.That(providers.Preferred<LightClientOptimisticUpdate>(peers, 0), Is.EqualTo(1));
        }
        providers.Failed<LightClientOptimisticUpdate>(peers[1]);
        providers.Failed<LightClientOptimisticUpdate>(peers[2]);
        Assert.That(providers.Maintain(Connected(peers)).Recycle, Is.SameAs(peers[1]));
    }

    [Test]
    public void Valid_older_updates_do_not_erase_recent_supplier_credit([Values] bool gossip)
    {
        ManualDeadlineClock clock = new();
        clock.SetSlot(1);
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock);
        PeerId source = new Identity().PeerId;
        BootstrapPeer[] peers = ProviderPeers(8, source.ToString());
        foreach (BootstrapPeer peer in peers) providers.Admit(peer, PeerDirection.Outbound);
        clock.SetSlot(14);
        foreach (BootstrapPeer peer in peers.Skip(1)) providers.RecordValidated(peer, ConsensusTests.Bootstrap(1, 1));
        LightClientStore store = OptimisticStore(ConsensusTests.Bootstrap(1, 1));

        foreach (ulong slot in new[] { 13UL, 3UL })
        {
            LightClientOptimisticUpdate update = Optimistic(slot);
            store.Process(update, 14);
            if (gossip) providers.RecordGossip(source, slot);
            else providers.RecordValidated(peers[0], update);
        }

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(13), "the older update is valid but does not advance consensus");
        Assert.That(providers.Maintain(Connected(peers)).Recycle, Is.Not.Null.And.Not.SameAs(peers[0]));
        Assert.That(providers.Preferred<LightClientOptimisticUpdate>(peers, 0), Is.EqualTo(gossip ? 1 : 0));
    }

    [Test]
    public async Task Recycling_an_old_session_cannot_drop_its_replacement_or_record_a_fault([Values] bool replaceSession)
    {
        using MemColumnsDb<BeaconChainDbColumns> db = new();
        await using BeaconPeerTransport transport = new(ConsensusTests.Spec, LimboLogs.Instance, db, quicPort: 0);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        PeerManager manager = (PeerManager)typeof(BeaconPeerTransport).GetField("_peers", flags)!.GetValue(transport)!;
        string id = new Identity().PeerId.ToString();
        string address = $"/ip4/127.0.0.1/tcp/9000/p2p/{id}";
        ISession oldSession = Substitute.For<ISession>();
        IBeaconSyncPeer old = Add(oldSession);
        ISession currentSession = replaceSession ? Substitute.For<ISession>() : oldSession;
        if (replaceSession) Add(currentSession);

        await manager.RecyclePeerAsync(old, "seeking another supplier", CancellationToken.None);

        Assert.That(manager.PeerCount, Is.EqualTo(replaceSession ? 1 : 0));
        if (replaceSession)
        {
            await oldSession.DidNotReceive().DisconnectAsync();
            await currentSession.DidNotReceive().DisconnectAsync();
            Assert.That(manager.GetPeerDiagnostics(), Is.Empty);
        }
        else
        {
            await oldSession.Received(1).DisconnectAsync();
            PeerManager.PeerDiagnostics diagnostics = manager.GetPeerDiagnostics().Single();
            using IDisposable scope = Assert.EnterMultipleScope();
            Assert.That(diagnostics.LastDisconnectReason, Is.EqualTo("TooManyPeers"));
            Assert.That(diagnostics.FailuresReported, Is.Zero);
            Assert.That(diagnostics.Banned, Is.False);
        }

        IBeaconSyncPeer Add(ISession session) => (IBeaconSyncPeer)typeof(PeerManager).GetMethod("AddPeerForTest", flags)!
            .Invoke(manager, [session, address, null, true])!;
    }

    [Test]
    public void Supplier_dial_hints_survive_restart_without_restoring_trust([Values] bool quic, [Values] bool gossip)
    {
        using MemDb db = new();
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BootstrapPeer peer = HintPeer(quic);
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock, metadata: db);
        providers.Admit(peer, PeerDirection.Outbound);
        if (gossip) providers.RecordGossip(Multiformats.Address.Multiaddress.Decode(peer.Id).GetPeerId()!, 13);
        else providers.RecordValidated(peer, ConsensusTests.Bootstrap(1, 1));
        providers.FlushHints();
        byte[] saved = db.Get(HintKey)!;
        ManualDeadlineClock restart = new();
        restart.SetSlot(14);
        restart.Advance(TimeSpan.FromMinutes(11));
        restart.ResetTimestamp();

        BeaconPeerTransport.PeerProviders restored = new(ConsensusTests.Spec, restart, metadata: db);
        Assert.That(restored.Preferred<LightClientBootstrap>([peer], 0), Is.EqualTo(-1), "import does not admit a session");
        restored.Admit(peer, PeerDirection.Outbound);
        string? occupied = restored.Maintain(Connected([peer])).Reconnect;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored.Preferred<LightClientBootstrap>([peer], 0), Is.EqualTo(-1));
            Assert.That(restored.Preferred<LightClientOptimisticUpdate>([peer], 0), Is.EqualTo(-1));
            Assert.That(occupied, Is.Null, "an occupied identity is not redialed");
        }
        Assert.That(restored.Maintain([]).Reconnect, Is.EqualTo(peer.Id));
        restored.FlushHints();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.Get(HintKey), Is.EqualTo(saved), "import and a redial must not extend authenticated useful time");
            Assert.That(db.WritesCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void Supplier_hints_require_current_outbound_authenticated_use(
        [Values] PeerDirection direction, [Values] bool replaced, [Values] bool validated)
    {
        using MemDb db = new();
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BootstrapPeer old = HintPeer();
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock, metadata: db);
        providers.Admit(old, direction);
        if (replaced) providers.Admit(new BootstrapPeer(ConsensusTests.Bootstrap(1, 1), old.Id), direction);
        if (validated) providers.RecordValidated(old, ConsensusTests.Bootstrap(1, 1));
        else providers.Failed<LightClientBootstrap>(old);
        providers.FlushHints();

        BeaconPeerTransport.PeerProviders restored = new(ConsensusTests.Spec, clock, metadata: db);

        Assert.That(restored.Maintain([]).Reconnect, Is.EqualTo(direction == PeerDirection.Outbound && !replaced && validated ? old.Id : null));
    }

    [TestCase(0, true)]
    [TestCase(86400, true)]
    [TestCase(86401, false)]
    [TestCase(-1, false)]
    public void Supplier_hint_import_preserves_the_original_utc_expiry(int ageSeconds, bool retained)
    {
        using MemDb db = new();
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BootstrapPeer peer = HintPeer();
        LearnHints(db, clock, peer).FlushHints();
        clock.Advance(TimeSpan.FromSeconds(ageSeconds));
        clock.ResetTimestamp();

        BeaconPeerTransport.PeerProviders restored = new(ConsensusTests.Spec, clock, metadata: db);

        Assert.That(restored.Maintain([]).Reconnect, Is.EqualTo(retained ? peer.Id : null));
    }

    [Test]
    public void Imported_hint_retention_uses_remaining_age_and_monotonic_elapsed_time()
    {
        using MemDb db = new();
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BootstrapPeer peer = HintPeer();
        LearnHints(db, clock, peer).FlushHints();
        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(30));
        clock.ResetTimestamp();
        BeaconPeerTransport.PeerProviders restored = new(ConsensusTests.Spec, clock, metadata: db);
        clock.MoveUtc(TimeSpan.FromDays(-2));
        Assert.That(restored.Maintain([]).Reconnect, Is.EqualTo(peer.Id));

        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.That(restored.Maintain([]).Reconnect, Is.Null, "UTC rollback cannot extend a loaded hint");
        restored.FlushHints();
        Assert.That(db.Get(HintKey)![36], Is.Zero);
    }

    private static readonly string[] InvalidHintCases =
    [
        "magic", "genesis", "count", "header", "entry", "address", "trailing", "oversize",
        "empty-address", "long-address", "utf8", "future", "negative", "expired", "peer-id", "protocol", "port"
    ];

    [Test]
    public void Invalid_supplier_hint_cache_cannot_supply_a_route([ValueSource(nameof(InvalidHintCases))] string corruption)
    {
        using MemDb db = new();
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BootstrapPeer peer = HintPeer();
        LearnHints(db, clock, peer).FlushHints();
        byte[] bytes = db.Get(HintKey)!.ToArray();
        long utc = clock.GetUtcNow().ToUnixTimeSeconds();
        switch (corruption)
        {
            case "magic": bytes[0] ^= 1; break;
            case "genesis": bytes[4] ^= 1; break;
            case "count": bytes[36] = 17; break;
            case "header": bytes = bytes[..36]; break;
            case "entry": bytes = bytes[..44]; break;
            case "address": bytes = bytes[..^1]; break;
            case "trailing": bytes = [.. bytes, 0]; break;
            case "oversize": bytes = new byte[16582]; break;
            case "empty-address": BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(45), 0); break;
            case "long-address": BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(45), 1025); break;
            case "utf8": bytes[47] = 0xff; break;
            case "future": BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(37), utc + 1); break;
            case "negative": BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(37), -1); break;
            case "expired": BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(37), utc - 86401); break;
            case "peer-id": bytes[^1] = (byte)'!'; break;
            case "protocol": Encoding.UTF8.GetBytes(peer.Id.Replace("/tcp/", "/udp/", StringComparison.Ordinal)).CopyTo(bytes.AsSpan(47)); break;
            case "port": Encoding.UTF8.GetBytes(peer.Id.Replace("/9000/", "/0000/", StringComparison.Ordinal)).CopyTo(bytes.AsSpan(47)); break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        db.Set(HintKey, bytes);

        BeaconPeerTransport.PeerProviders restored = new(ConsensusTests.Spec, clock, metadata: db);
        string? reconnect = restored.Maintain([]).Reconnect;

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(reconnect, Is.Null);
        Assert.That(restored.Preferred<LightClientBootstrap>([peer], 0), Is.EqualTo(-1));
    }

    [Test]
    public void Duplicate_hint_identities_keep_one_deterministic_route([Values(-1, 0, 1)] int secondNewer)
    {
        using MemDb db = new();
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BootstrapPeer peer = HintPeer();
        LearnHints(db, clock, peer).FlushHints();
        byte[] first = db.Get(HintKey)!.ToArray();
        long utc = clock.GetUtcNow().ToUnixTimeSeconds();
        BinaryPrimitives.WriteInt64BigEndian(first.AsSpan(37), utc - 2);
        byte[] second = first[37..];
        BinaryPrimitives.WriteInt64BigEndian(second, utc - 2 + secondNewer);
        string alternate = peer.Id.Replace("/9000/", "/9001/", StringComparison.Ordinal);
        Encoding.UTF8.GetBytes(alternate).CopyTo(second.AsSpan(10));
        byte[] duplicate = [.. first, .. second];
        duplicate[36] = 2;
        db.Set(HintKey, duplicate);

        BeaconPeerTransport.PeerProviders restored = new(ConsensusTests.Spec, clock, metadata: db);

        Assert.That(restored.Maintain([]).Reconnect, Is.EqualTo(secondNewer > 0 ? alternate : peer.Id));
        Assert.That(restored.Maintain([]).Reconnect, Is.Null, "the same identity cannot occupy two hint slots");
    }

    [Test]
    public void Persisted_supplier_hints_remain_bounded_and_rotate_each_untried_identity()
    {
        using MemDb db = new();
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BootstrapPeer[] peers = Enumerable.Range(0, 17).Select(_ => HintPeer()).ToArray();
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock, metadata: db);
        foreach (BootstrapPeer peer in peers)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            providers.Admit(peer, PeerDirection.Outbound);
            providers.RecordValidated(peer, ConsensusTests.Bootstrap(1, 1));
        }
        providers.FlushHints();
        BeaconPeerTransport.PeerProviders restored = new(ConsensusTests.Spec, clock, metadata: db);
        List<string?> requested = [];
        for (int i = 0; i < 17; i++) requested.Add(restored.Maintain([]).Reconnect);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(db.Get(HintKey)!.Length, Is.LessThanOrEqualTo(16581));
        Assert.That(db.Get(HintKey)![36], Is.EqualTo(16));
        Assert.That(requested, Is.EqualTo(peers.Skip(1).Reverse().Select(peer => peer.Id).Append(null)));
    }

    [Test]
    public void Failed_hint_redials_rotate_before_retry_and_new_authenticated_use_restores_priority()
    {
        ManualDeadlineClock clock = new();
        clock.SetSlot(1);
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock);
        BootstrapPeer[] peers = ProviderPeers(3);
        foreach (BootstrapPeer peer in peers.Reverse())
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            providers.Admit(peer, PeerDirection.Outbound);
            providers.RecordValidated(peer, ConsensusTests.Bootstrap(1, 1));
        }
        List<string?> requested = [];
        for (int i = 0; i < 4; i++)
        {
            requested.Add(providers.Maintain([]).Reconnect);
            clock.Advance(TimeSpan.FromSeconds(15));
        }
        Assert.That(requested, Is.EqualTo(new[] { peers[0].Id, peers[1].Id, peers[2].Id, peers[0].Id }),
            "failed A/B attempts must not repeatedly skip the third route");
        providers.Admit(peers[0], PeerDirection.Outbound);
        providers.RecordValidated(peers[0], ConsensusTests.Bootstrap(1, 1));

        Assert.That(providers.Maintain([]).Reconnect, Is.EqualTo(peers[0].Id), "new authenticated usefulness regains untried priority even during its prior dial cooldown");
    }

    [Test]
    public void Unavailable_hint_storage_does_not_block_current_supplier_learning()
    {
        using HintDb db = new() { FailRead = true };
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BootstrapPeer peer = HintPeer();
        BeaconPeerTransport.PeerProviders providers = LearnHints(db, clock, peer);
        Assert.That(providers.Maintain([]).Reconnect, Is.EqualTo(peer.Id));
        providers.FlushHints();
        db.FailRead = false;

        BeaconPeerTransport.PeerProviders restored = new(ConsensusTests.Spec, clock, metadata: db);

        Assert.That(restored.Maintain([]).Reconnect, Is.EqualTo(peer.Id));
    }

    [Test]
    public void Failed_hint_saves_preserve_last_good_metadata_and_retry_at_a_bounded_cadence()
    {
        using HintDb db = new();
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BootstrapPeer first = HintPeer();
        BeaconPeerTransport.PeerProviders providers = LearnHints(db, clock, first);
        providers.FlushHints();
        byte[] saved = db.Get(HintKey)!.ToArray();
        clock.Advance(TimeSpan.FromSeconds(1));
        BootstrapPeer second = HintPeer();
        providers.Admit(second, PeerDirection.Outbound);
        providers.RecordValidated(second, ConsensusTests.Bootstrap(1, 1));
        providers.FlushHints();
        clock.Advance(TimeSpan.FromSeconds(58));
        providers.FlushHints();
        Assert.That(db.Attempts, Is.EqualTo(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        db.Fail = true;
        providers.FlushHints();
        providers.FlushHints();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.Attempts, Is.EqualTo(2));
            Assert.That(db.Get(HintKey), Is.EqualTo(saved));
        }
        clock.Advance(TimeSpan.FromMinutes(1));
        db.Fail = false;
        providers.FlushHints();
        BeaconPeerTransport.PeerProviders restored = new(ConsensusTests.Spec, clock, metadata: db);
        string?[] requested = [restored.Maintain([]).Reconnect, restored.Maintain([]).Reconnect];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.Attempts, Is.EqualTo(3));
            Assert.That(requested, Is.EqualTo(new[] { second.Id, first.Id }));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Hint_snapshot_acknowledgement_preserves_concurrent_usefulness(
        [Values] bool failWrite, [Values] bool sameIdentity, CancellationToken token)
    {
        using HintDb db = new();
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BootstrapPeer first = HintPeer();
        BeaconPeerTransport.PeerProviders providers = LearnHints(db, clock, first);
        db.Fail = failWrite;
        db.BeforeSave = () => { entered.SetResult(); release.Wait(token); };
        Task save = Task.Run(() => providers.FlushHints());
        BootstrapPeer second = sameIdentity
            ? new BootstrapPeer(ConsensusTests.Bootstrap(1, 1), first.Id.Replace("/9000/", "/9001/", StringComparison.Ordinal))
            : HintPeer();
        try
        {
            await Task.WhenAny(entered.Task, save).WaitAsync(token);
            if (save.IsCompleted) await save;
            Assert.That(entered.Task.IsCompleted, Is.True, "the production snapshot must reach the metadata write");
            clock.Advance(TimeSpan.FromSeconds(1));
            providers.Admit(second, PeerDirection.Outbound);
            providers.RecordValidated(second, ConsensusTests.Bootstrap(1, 1));
        }
        finally { release.Set(); await save; }
        db.BeforeSave = null;
        db.Fail = false;
        clock.Advance(TimeSpan.FromMinutes(1));
        providers.FlushHints();
        BeaconPeerTransport.PeerProviders restored = new(ConsensusTests.Spec, clock, metadata: db);

        string?[] requested = [restored.Maintain([]).Reconnect, restored.Maintain([]).Reconnect];
        Assert.That(requested, Is.EqualTo(new[] { second.Id, sameIdentity ? null : first.Id }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Beacon_transport_joins_hint_writer_then_flushes_new_usefulness_before_disposal(CancellationToken token)
    {
        using MemColumnsDb<BeaconChainDbColumns> backing = new();
        using HintDb db = new();
        using ManualResetEventSlim release = new();
        using IColumnsDb<BeaconChainDbColumns> columns = Substitute.For<IColumnsDb<BeaconChainDbColumns>>();
        columns.GetColumnDb(Arg.Any<BeaconChainDbColumns>()).Returns(call =>
            (BeaconChainDbColumns)call[0] == BeaconChainDbColumns.Metadata ? db : backing.GetColumnDb((BeaconChainDbColumns)call[0]));
        ManualDeadlineClock clock = new();
        clock.SetSlot(14);
        BeaconPeerTransport transport = new(ConsensusTests.Spec, LimboLogs.Instance, columns, time: clock, quicPort: 0);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        BeaconPeerTransport.PeerProviders providers = (BeaconPeerTransport.PeerProviders)typeof(BeaconPeerTransport).GetField("_providers", flags)!.GetValue(transport)!;
        BootstrapPeer first = HintPeer();
        BootstrapPeer second = HintPeer();
        Task? save = null;
        Task? shutdown = null;
        try
        {
            providers.Admit(first, PeerDirection.Outbound);
            providers.RecordValidated(first, ConsensusTests.Bootstrap(1, 1));
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            db.BeforeSave = () => { db.BeforeSave = null; entered.SetResult(); release.Wait(token); };
            save = Task.Run(() => providers.FlushHints());
            typeof(BeaconPeerTransport).GetField("_peerLoop", flags)!.SetValue(transport, save);
            await Task.WhenAny(entered.Task, save).WaitAsync(token);
            if (save.IsCompleted) await save;
            Assert.That(entered.Task.IsCompleted, Is.True, "the owned writer must reach the metadata write");
            clock.Advance(TimeSpan.FromSeconds(1));
            providers.Admit(second, PeerDirection.Outbound);
            providers.RecordValidated(second, ConsensusTests.Bootstrap(1, 1));
            shutdown = transport.DisposeAsync().AsTask();
            Assert.That(shutdown.IsCompleted, Is.False, "shutdown must join the owned writer");
        }
        finally
        {
            release.Set();
            try { await (shutdown ?? transport.DisposeAsync().AsTask()); }
            finally { if (save is not null) await save; }
        }
        clock.Advance(TimeSpan.FromMinutes(1));
        BootstrapPeer late = HintPeer();
        providers.Admit(late, PeerDirection.Outbound);
        providers.RecordValidated(late, ConsensusTests.Bootstrap(1, 1));
        providers.FlushHints();
        BeaconPeerTransport.PeerProviders restored = new(ConsensusTests.Spec, clock, metadata: db);
        string?[] requested = [restored.Maintain([]).Reconnect, restored.Maintain([]).Reconnect, restored.Maintain([]).Reconnect];

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(db.Attempts, Is.EqualTo(2));
        Assert.That(requested, Is.EqualTo(new[] { second.Id, first.Id, null }), "late callbacks cannot save after shutdown");
    }

    private static ReadOnlySpan<byte> HintKey => "light-client-supplier-hints/v1"u8;

    private static BootstrapPeer HintPeer(bool quic = false) => new(ConsensusTests.Bootstrap(1, 1),
        $"/ip4/127.0.0.1/{(quic ? "udp/9000/quic-v1" : "tcp/9000")}/p2p/{new Identity().PeerId}");

    private static BeaconPeerTransport.PeerProviders LearnHints(IDb db, ManualDeadlineClock clock, params BootstrapPeer[] peers)
    {
        BeaconPeerTransport.PeerProviders providers = new(ConsensusTests.Spec, clock, metadata: db);
        foreach (BootstrapPeer peer in peers)
        {
            providers.Admit(peer, PeerDirection.Outbound);
            providers.RecordValidated(peer, ConsensusTests.Bootstrap(1, 1));
        }
        return providers;
    }

    private sealed class HintDb : MemDb
    {
        internal bool Fail;
        internal bool FailRead;
        internal Action? BeforeSave;
        internal int Attempts;

        public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            if (FailRead && key.SequenceEqual(HintKey)) throw new IOException("Injected optional hint read failure.");
            return base.Get(key, flags);
        }

        public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (key.SequenceEqual(HintKey))
            {
                Attempts++;
                BeforeSave?.Invoke();
                if (Fail) throw new IOException("Injected optional hint write failure.");
            }
            base.Set(key, value, flags);
        }
    }

    private static BootstrapPeer[] ProviderPeers(int count, string first = "first") => Enumerable.Range(0, count)
        .Select(index => new BootstrapPeer(ConsensusTests.Bootstrap(1, 1), $"/ip4/127.0.0.1/tcp/9000/p2p/{(index == 0 ? first : index.ToString())}")).ToArray();

    private static PeerRecord[] Connected(IEnumerable<BootstrapPeer> peers) => peers.Select(peer => new PeerRecord(
        peer.Id[(peer.Id.LastIndexOf("/p2p/", StringComparison.Ordinal) + 5)..], PeerDirection.Outbound,
        PeerConnectionState.Connected, peer.Id, null, null)).ToArray();

    private static Func<T, CancellationToken, Task<bool>> Validate<T>(Action<T> validate) => (value, _) =>
    {
        validate(value);
        return Task.FromResult(true);
    };

    private static Task<bool> AdvanceOptimistic(LightClientStore store, LightClientOptimisticUpdate update)
    {
        ulong before = store.OptimisticHeader.Beacon!.Slot;
        store.Process(update, 14);
        return Task.FromResult(store.OptimisticHeader.Beacon!.Slot > before);
    }

    private static LightClientStore OptimisticStore(LightClientBootstrap bootstrap)
    {
        LightClientStore store = new(ConsensusTests.Spec, SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!), bootstrap, 12);
        store.Process(Optimistic(10), 12);
        return store;
    }

    private static LightClientOptimisticUpdate Optimistic(ulong slot, int participants = 342, byte key = 1)
    {
        LightClientUpdate signed = ConsensusTests.Update(1, slot, slot + 1, key, participants);
        return new()
        {
            AttestedHeader = signed.AttestedHeader,
            SyncAggregate = signed.SyncAggregate,
            SignatureSlot = signed.SignatureSlot,
        };
    }

    private sealed class BootstrapPeer(LightClientBootstrap bootstrap, string id = "light-client-test") : IBeaconSyncPeer
    {
        public List<PeerFailureReason> Failures { get; } = [];
        public string Id => id;
        public ulong HeadSlot => 1;
        public PeerColumnCustody Custody => default!;

        public Task<LightClientBootstrap> RequestLightClientBootstrapAsync(Hash256 root, CancellationToken token) =>
            Task.FromResult(bootstrap);

        public void ReportFailure(PeerFailureReason reason, string? detail = null) => Failures.Add(reason);

        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count,
            CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] roots,
            CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count,
            ulong[] columns, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(
            DataColumnsByRootIdentifier[] identifiers, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRangeAsync(ulong startSlot,
            ulong count, ulong[] columns, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRootAsync(
            DataColumnsByRootIdentifier[] identifiers, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRangeAsync(
            ulong startSlot, ulong count, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRootAsync(
            Hash256[] roots, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class ManualDeadlineClock : TimeProvider
    {
        private DateTimeOffset _now;
        private long _timestamp;
        private TimerCallback? _callback;
        private object? _state;

        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void SetSlot(ulong slot)
        {
            DateTimeOffset next = DateTimeOffset.FromUnixTimeSeconds((long)(ConsensusTests.Spec.GenesisTime + slot * ConsensusTests.Spec.SecondsPerSlot));
            Advance(next - _now);
        }
        internal void Advance(TimeSpan elapsed) { _now += elapsed; _timestamp += elapsed.Ticks; }
        internal void MoveUtc(TimeSpan elapsed) => _now += elapsed;
        internal void ResetTimestamp() => _timestamp = 0;

        public TimeSpan DueTime { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            return new NoopTimer();
        }

        public void Expire() => _callback!(_state);

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }
}
