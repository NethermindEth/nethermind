// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.LightClient.Consensus;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;

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
        Assert.That(async () => await BeaconPeerTransport.RequestFromPeersAsync<string>([peer],
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

        (LightClientBootstrap? response, _) = await BeaconPeerTransport.RequestFromPeersAsync<LightClientBootstrap>(
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

        (string? selected, _) = await BeaconPeerTransport.RequestFromPeersAsync<string>([stale, current],
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

        string? response = await BeaconPeerTransport.PollFromPeersAsync<string>(() => [first, second],
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

        LightClientOptimisticUpdate? selected = await BeaconPeerTransport.PollFromPeersAsync<LightClientOptimisticUpdate>(
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

        LightClientFinalityUpdate? selected = await BeaconPeerTransport.PollFromPeersAsync<LightClientFinalityUpdate>(
            () => [first, second], (peer, _) =>
            {
                requested.Add(peer.Id);
                return Task.FromResult(update);
            }, Validate<LightClientFinalityUpdate>(value => store.Process(value, 12)), CancellationToken.None);

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

        Task<LightClientOptimisticUpdate?> poll = BeaconPeerTransport.PollFromPeersAsync<LightClientOptimisticUpdate>(
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

        Task<string?> poll = BeaconPeerTransport.PollFromPeersAsync<string>(() => peers,
            (value, _) => Task.FromResult(value.Id), static (_, _) => Task.FromResult(true), CancellationToken.None, clock);

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

        Task<string?> poll = BeaconPeerTransport.PollFromPeersAsync<string>(() =>
        {
            snapshots++;
            return [];
        }, static (_, _) => Task.FromResult("unreachable"), static (_, _) => Task.FromResult(true), caller.Token, new ManualDeadlineClock());
        caller.Cancel();

        Assert.That(async () => await poll, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(snapshots, Is.EqualTo(alreadyCanceled ? 0 : 1));
    }

    [Test]
    public void Poll_with_only_failed_requests_reports_unavailability()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        BootstrapPeer peer = new(bootstrap);

        Assert.That(async () => await BeaconPeerTransport.PollFromPeersAsync<string>(() => [peer],
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

        Task<string?> poll = BeaconPeerTransport.PollFromPeersAsync<string>(() => [stalled, available], async (peer, token) =>
        {
            if (peer == stalled) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return peer.Id;
        }, static (_, _) => Task.FromResult(true), CancellationToken.None, clock);

        Assert.That(clock.DueTime, Is.EqualTo(TimeSpan.FromSeconds(5)));
        clock.Expire();
        string? response = await poll;

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(response, Is.EqualTo(available.Id));
        Assert.That(stalled.Failures, Is.EqualTo([PeerFailureReason.RequestFailed]));
        Assert.That(available.Failures, Is.Empty);
    }

    [Test]
    public void Caller_cancellation_does_not_penalize_a_peer()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        BootstrapPeer peer = new(bootstrap);
        using CancellationTokenSource caller = new();

        Task<(string? Response, bool SawInapplicable)> request = BeaconPeerTransport.RequestFromPeersAsync<string>(
            [peer], async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "unreachable";
            }, static (_, _) => Task.FromResult(true), caller.Token, new ManualDeadlineClock());
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

        (LightClientUpdate? response, _) = await BeaconPeerTransport.RequestFromPeersAsync<LightClientUpdate>(
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

        (LightClientBootstrap? response, _) = await BeaconPeerTransport.RequestFromPeersAsync<LightClientBootstrap>(
            [peer], (value, token) => value.RequestLightClientBootstrapAsync(checkpoint, token),
            Validate<LightClientBootstrap>(value => _ = new LightClientStore(ConsensusTests.Spec, checkpoint, value, currentSlot)), CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(response, Is.Null);
        Assert.That(peer.Failures, Is.Empty);
    }

    private static Func<T, CancellationToken, Task<bool>> Validate<T>(Action<T> validate) => (value, _) =>
    {
        validate(value);
        return Task.FromResult(true);
    };

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
        private TimerCallback? _callback;
        private object? _state;

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
