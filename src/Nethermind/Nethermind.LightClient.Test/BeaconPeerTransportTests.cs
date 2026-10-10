// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.LightClient.Consensus;

namespace Nethermind.LightClient.Test;

public class BeaconPeerTransportTests
{
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
            bootstrap => _ = new LightClientStore(ConsensusTests.Spec, checkpoint, bootstrap, 1), CancellationToken.None);

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
            (peer, _) => Task.FromResult(peer.Id), id =>
            {
                if (id == "stale") throw new IrrelevantLightClientUpdateException();
            }, CancellationToken.None);

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

        string? response = await BeaconPeerTransport.PollFromPeersAsync<string>([first, second],
            (peer, _) => Task.FromResult(peer.Id), _ =>
            {
                if (irrelevant) throw new IrrelevantLightClientUpdateException();
                throw new LightClientLocalStateException("Committee unavailable.");
            }, CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(response, Is.Null);
        Assert.That(first.Failures, Is.Empty);
        Assert.That(second.Failures, Is.Empty);
    }

    [Test]
    public void Poll_with_only_failed_requests_reports_unavailability()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        BootstrapPeer peer = new(bootstrap);

        Assert.That(async () => await BeaconPeerTransport.PollFromPeersAsync<string>([peer],
            (_, _) => Task.FromException<string>(new IOException("Request failed.")), _ => { }, CancellationToken.None),
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

        Task<string?> poll = BeaconPeerTransport.PollFromPeersAsync<string>([stalled, available], async (peer, token) =>
        {
            if (peer == stalled) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return peer.Id;
        }, _ => { }, CancellationToken.None, clock);

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
            }, _ => { }, caller.Token, new ManualDeadlineClock());
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
            [peer], (_, _) => Task.FromResult(update), value => local.Process(value, localSlot), CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(reference.FinalizedHeader.Beacon!.Slot, Is.EqualTo(finalizedSlot));
        Assert.That(response, Is.Null);
        Assert.That(peer.Failures, Is.Empty);
        Assert.That(local.FinalizedHeader.Beacon!.Slot, Is.EqualTo(1));
    }

    [TestCase(0UL)]
    [TestCase(100802UL)]
    public async Task Locally_inapplicable_checkpoint_does_not_penalize_peer(ulong currentSlot)
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        BootstrapPeer peer = new(bootstrap);

        (LightClientBootstrap? response, _) = await BeaconPeerTransport.RequestFromPeersAsync<LightClientBootstrap>(
            [peer], (value, token) => value.RequestLightClientBootstrapAsync(checkpoint, token),
            value => _ = new LightClientStore(ConsensusTests.Spec, checkpoint, value, currentSlot), CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(response, Is.Null);
        Assert.That(peer.Failures, Is.Empty);
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
