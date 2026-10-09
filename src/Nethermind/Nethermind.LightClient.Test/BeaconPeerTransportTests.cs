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

        LightClientBootstrap? response = await BeaconPeerTransport.RequestFromPeersAsync<LightClientBootstrap>(
            [bad, good], peer => peer.RequestLightClientBootstrapAsync(checkpoint, CancellationToken.None),
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

        string? selected = await BeaconPeerTransport.RequestFromPeersAsync<string>([stale, current],
            peer => Task.FromResult(peer.Id), id =>
            {
                if (id == "stale") throw new IrrelevantLightClientUpdateException();
            }, CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(selected, Is.EqualTo("current"));
        Assert.That(stale.Failures, Is.Empty);
        Assert.That(current.Failures, Is.Empty);
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

        LightClientUpdate? response = await BeaconPeerTransport.RequestFromPeersAsync<LightClientUpdate>(
            [peer], _ => Task.FromResult(update), value => local.Process(value, localSlot), CancellationToken.None);

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

        LightClientBootstrap? response = await BeaconPeerTransport.RequestFromPeersAsync<LightClientBootstrap>(
            [peer], value => value.RequestLightClientBootstrapAsync(checkpoint, CancellationToken.None),
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
}
