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
