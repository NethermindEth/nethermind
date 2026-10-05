// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NSubstitute;
using Snappier;
using static Nethermind.BeaconChain.Test.P2P.RangeSyncTests;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

public class ColumnGossipRouterCensorshipTests
{
    private static readonly byte[] ProposerMasterKey = Bytes.FromHexString("0x2cd4ba406b522459d57a0bed51a397435c0bb11dd5f3ca1152b3694bb91d7c22");

    [Test]
    [CancelAfter(60_000)]
    public async Task A_column_censored_over_gossip_is_recovered_by_root_when_its_block_is_deferred([Values] bool checkedHeader, CancellationToken token)
    {
        await using DeferredBlockColumnFetchTests.Fixture fixture = DeferredBlockColumnFetchTests.Fixture.Create();
        ulong censored = fixture.Sampled[0];
        ColumnGossipRouter router = checkedHeader ? RouterCheckingHeaders(fixture) : new(fixture.Chain.Spec, fixture.Clock, LimboLogs.Instance, fixture.SidecarPool);
        router.Start(_ => Substitute.For<ITopic>(), [0, 0, 0, 0], fixture.Sampled);
        // The fixture anchor's genesis validators root is not the spec's, so its block signature is not one the router's domain accepts.
        BlsSignature signature = SignHeader(fixture.Chain.Columns[0].SignedBlockHeader!.Message!, fixture.Chain.Spec);
        DataColumnSidecar Gossiped(DataColumnSidecar sidecar)
        {
            if (!checkedHeader)
            {
                return sidecar;
            }

            DataColumnSidecar.Decode(DataColumnSidecar.Encode(sidecar), out DataColumnSidecar signed);
            signed.SignedBlockHeader!.Signature = signature;
            return signed;
        }

        foreach (ulong column in fixture.Sampled.Where(c => c != censored))
        {
            router.Handle(column, gloasTopic: false, Message(Gossiped(fixture.Chain.Columns[(int)column])));
        }

        for (int copy = 0; copy < ColumnGossipRouter.KzgBatchesPerColumn; copy++)
        {
            router.Handle(censored, gloasTopic: false, Message(Gossiped(Altered(fixture.Chain.Columns[(int)censored], copy))));
        }

        MessageValidity honestOverGossip = router.Handle(censored, gloasTopic: false, Message(Gossiped(fixture.Chain.Columns[(int)censored])));
        bool pooledOverGossip = fixture.SidecarPool.TryGet(fixture.Chain.BlockRoot, censored, out _);
        StubPeer custodian = new("custodian", fixture.Chain.Block.Message!.Slot, static (_, _) => [], custody: new PeerColumnCustody(fixture.Sampled, isAdvertised: true),
            rootHandler: identifiers => [.. identifiers.Single().Columns!.Select(c => fixture.Chain.Columns[(int)c])]);
        fixture.Peers.Add(custodian);
        BeaconSyncOrchestrator orchestrator = fixture.CreateOrchestrator();

        BlockImportResult result = await orchestrator.ImportAndSettleAsync(fixture.Importer, new ForkedSignedBeaconBlock.OfFulu(fixture.Chain.Block), token);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((honestOverGossip, pooledOverGossip), Is.EqualTo((MessageValidity.Ignored, false)), "the honest copy is refused once the bound is spent");
        Assert.That(router.GetDropCount(ColumnGossipDropReason.KzgBatchLimit), Is.EqualTo(1));
        Assert.That(router.HeaderSignatureVerificationCount, Is.EqualTo(checkedHeader ? 1 : 0), "the copies passed the header signature check, so only the KZG bound stopped the honest one");
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
        Assert.That(custodian.RequestedColumns, Is.EqualTo(new[] { new[] { censored } }), "only the censored column is fetched");
        Assert.That(fixture.SidecarPool.TryGet(fixture.Chain.BlockRoot, censored, out DataColumnSidecar? recovered) && ReferenceEquals(recovered, fixture.Chain.Columns[(int)censored]), Is.True);
    }

    private static ColumnGossipRouter RouterCheckingHeaders(DeferredBlockColumnFetchTests.Fixture fixture)
    {
        CheckpointRef anchor = new(0, fixture.Chain.AnchorRoot);
        ForkChoiceSnapshotNode node = new(0, fixture.Chain.AnchorRoot, null, 0, 0, 0, ExecutionStatus.Valid, Hash256.Zero);
        ForkChoiceSnapshotHolder forkChoice = new() { Current = new ForkChoiceSnapshot(anchor, anchor, Hash256.Zero, [node]) };
        ProposerLookaheadHolder lookahead = new() { Current = new ProposerLookaheadSnapshot(0, fixture.Chain.AnchorRoot, new ulong[Presets.ProposerLookaheadSlots]) };
        return new ColumnGossipRouter(fixture.Chain.Spec, fixture.Clock, LimboLogs.Instance, fixture.SidecarPool, forkChoice: forkChoice, pubkeys: fixture.Chain.Pubkeys, proposerLookahead: lookahead);
    }

    private static BlsSignature SignHeader(BeaconBlockHeader header, BeaconChainSpec spec)
    {
        Bls.SecretKey proposerKey = new(new Bls.SecretKey(ProposerMasterKey, Bls.ByteOrder.LittleEndian), 0);
        Hash256 domain = Domains.ComputeDomain(DomainType.BeaconProposer, spec.VersionForEpoch(spec.GetEpoch(header.Slot)), spec.GenesisValidatorsRoot);
        return new BlsSignature(BlsSigner.Sign(proposerKey, Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(header), domain).Bytes).Bytes);
    }

    private static byte[] Message(DataColumnSidecar sidecar) => Snappy.CompressToArray(DataColumnSidecar.Encode(sidecar));

    private static DataColumnSidecar Altered(DataColumnSidecar honest, int variant)
    {
        DataColumnSidecar.Decode(DataColumnSidecar.Encode(honest), out DataColumnSidecar altered);
        // The low byte of the first field element, so the cell stays canonical and only its proof fails.
        altered.Column![0][31] ^= (byte)(1 + variant);
        return altered;
    }
}
