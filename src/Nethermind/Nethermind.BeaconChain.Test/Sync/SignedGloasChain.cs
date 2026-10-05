// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;
using FuluStateTransition = Nethermind.BeaconChain.StateTransition.StateTransition;

namespace Nethermind.BeaconChain.Test.Sync;

internal sealed class SignedGloasChain(IBeaconStateHasher? hasher = null)
{
    // The importer hashes incrementally, so a test of its roots passes a full hasher to compare against.
    private readonly IBeaconStateHasher _hasher = hasher ?? new CachedBeaconStateHasher();

    public BeaconChainSpec Spec => ForkCrossingChain.Instance.Spec;

    /// <summary>Copy the shared anchor so importer mutation cannot contaminate other cases.</summary>
    public BeaconStateFulu AnchorState { get; } = ForkCrossingChain.Instance.AnchorState.Clone();

    public SignedBeaconBlock AnchorBlock { get; } = new() { Message = ForkCrossingChain.Instance.AnchorBlock, Signature = default };

    public Hash256 AnchorRoot => ForkCrossingChain.Instance.AnchorRoot;

    public sealed record Block(SignedBeaconBlockGloas Signed, Hash256 Root, BeaconStateGloas PostState, SignedExecutionPayloadEnvelope Envelope)
    {
        public ForkedSignedBeaconBlock Forked => new ForkedSignedBeaconBlock.OfGloas(Signed);

        public ExecutionPayloadBid Bid => Signed.Message!.Body!.SignedExecutionPayloadBid!.Message!;
    }

    public sealed record FuluBlock(SignedBeaconBlock Signed, Hash256 Root, BeaconStateFulu PostState)
    {
        public ForkedSignedBeaconBlock Forked => new ForkedSignedBeaconBlock.OfFulu(Signed);
    }

    public FuluBlock NextFulu(ulong slot, FuluBlock? parent = null, byte blockHashFill = 0xE0)
    {
        BeaconStateFulu parentState = parent?.PostState ?? AnchorState;
        BeaconStateFulu preState = parentState.Clone();
        SlotProcessing.ProcessSlots(preState, slot, new EpochCache());
        ulong epoch = preState.GetCurrentEpoch();
        BeaconBlock message = TestChain.CreateBlock(slot, SszRoots.HashTreeRoot(preState.LatestBlockHeader!)).Message!;
        message.ProposerIndex = (ulong)preState.GetBeaconProposerIndex();
        Bls.SecretKey proposerKey = ValidatorKey((int)message.ProposerIndex);
        BeaconBlockBody body = message.Body!;
        body.RandaoReveal = Sign(proposerKey, Domains.ComputeSigningRoot(ImportableBlobBlock.EpochRoot(epoch), preState.GetDomain(DomainType.Randao, epoch)));
        body.SyncAggregate!.SyncCommitteeSignature = new BlsSignature(SignatureSets.G2PointAtInfinity);
        Nethermind.BeaconChain.Types.ExecutionPayload payload = body.ExecutionPayload!;
        payload.ParentHash = preState.LatestExecutionPayloadHeader!.BlockHash;
        payload.PrevRandao = preState.GetRandaoMix(epoch);
        payload.Timestamp = preState.GenesisTime + slot * Presets.SecondsPerSlot;
        payload.BlockHash = Hash(blockHashFill);
        Hash256 proposerDomain = preState.GetDomain(DomainType.BeaconProposer, epoch);

        BeaconStateFulu postState = parentState.Clone();
        FuluStateTransition.Apply(postState, new SignedBeaconBlock { Message = message }, new EpochCache(), new PubkeyCache(), new AcceptingNotifier(), Spec, validateResult: false, verifySignatures: false);
        message.StateRoot = SszRoots.HashTreeRoot(postState);
        Hash256 root = SszRoots.HashTreeRoot(message);
        return new FuluBlock(new SignedBeaconBlock { Message = message, Signature = Sign(proposerKey, Domains.ComputeSigningRoot(root, proposerDomain)) }, root, postState);
    }

    public Block Next(Block? parent, ulong slot, bool full, byte blockHashFill, SszKzgCommitment[]? blobCommitments = null, System.Func<BeaconStateGloas, EpochCache, AttestationGloas[]>? attestations = null, PayloadAttestation[]? payloadAttestations = null) =>
        parent is null
            ? Build(CrossFork(AnchorState), slot, full, blockHashFill, blobCommitments, attestations, payloadAttestations)
            : Build(parent.PostState.Clone(), slot, full, blockHashFill, blobCommitments, attestations, payloadAttestations);

    public Block NextOnFulu(FuluBlock parent, ulong slot, bool full, byte blockHashFill) => Build(CrossFork(parent.PostState), slot, full, blockHashFill, null);

    private BeaconStateGloas CrossFork(BeaconStateFulu fuluParent)
    {
        BeaconStateFulu fulu = fuluParent.Clone();
        SlotProcessing.ProcessSlots(fulu, BoundarySlot, new EpochCache { Hasher = _hasher });
        return GloasForkTransition.UpgradeToGloas(fulu, Spec);
    }

    private Block Build(BeaconStateGloas state, ulong slot, bool full, byte blockHashFill, SszKzgCommitment[]? blobCommitments, System.Func<BeaconStateGloas, EpochCache, AttestationGloas[]>? attestations = null, PayloadAttestation[]? payloadAttestations = null)
    {
        EpochCache cache = new() { Hasher = _hasher };
        if (state.Slot < slot)
        {
            GloasSlotProcessing.ProcessSlots(state, slot, cache);
        }

        SignedExecutionPayloadBid bid = SelfBuildBid(state, full ? state.LatestExecutionPayloadBid!.BlockHash! : state.LatestBlockHash!, Hash(blockHashFill));
        if (blobCommitments is not null)
        {
            bid.Message!.BlobKzgCommitments = blobCommitments;
        }

        SignedBeaconBlockGloas block = MinimalBlock(state, bid);
        BeaconBlockGloas message = block.Message!;
        if (attestations is not null)
        {
            message.Body!.Attestations = attestations(state, cache);
        }

        if (payloadAttestations is not null)
        {
            message.Body!.PayloadAttestations = payloadAttestations;
        }

        Bls.SecretKey proposerKey = ValidatorKey((int)message.ProposerIndex);
        ulong epoch = state.GetCurrentEpoch();
        message.Body!.RandaoReveal = Sign(proposerKey, Domains.ComputeSigningRoot(ImportableBlobBlock.EpochRoot(epoch), state.GetDomain(DomainType.Randao, epoch)));
        Hash256 proposerDomain = state.GetDomain(DomainType.BeaconProposer, epoch);

        GloasBlockProcessing.ProcessBlock(state, message, cache, new PubkeyCache(), new AcceptingNotifier(), Spec, verifySignatures: false);
        message.StateRoot = _hasher.HashTreeRoot(state);
        Hash256 root = SszRoots.HashTreeRoot(message);
        block.Signature = Sign(proposerKey, Domains.ComputeSigningRoot(root, proposerDomain));

        return new Block(block, root, state, ValidEnvelope(state, bid.Message!, proposerKey, Presets.BuilderIndexSelfBuild, root));
    }

    public BlockImporter CreateImporter(IEngineDriver engine, System.Func<Hash256, ExecutionPayloadBid, bool>? isEnvelopeDataAvailable = null, ForkChoiceSnapshotHolder? snapshots = null, BeaconChainStore? store = null, ILogManager? logManager = null, SlotClock? clock = null, FailedBlockRoots? failedBlocks = null, Block? gloasAnchor = null, FuluBlock? fuluAnchor = null)
    {
        PubkeyCache pubkeys = new();
        pubkeys.Build((fuluAnchor?.PostState ?? AnchorState).Validators!);
        return new BlockImporter(
            Spec,
            store ?? CreateStore(),
            pubkeys,
            engine,
            new BeaconChainConfig(),
            logManager ?? LimboLogs.Instance,
            ReplayedBlockAvailability.Instance,
            isEnvelopeDataAvailable ?? (static (_, _) => true),
            clock ?? new SlotClock(Spec, Timestamper.Default),
            gloasAnchor is null ? new ForkedBeaconState.OfFulu(fuluAnchor?.PostState ?? AnchorState) : new ForkedBeaconState.OfGloas(gloasAnchor.PostState),
            gloasAnchor?.Forked ?? new ForkedSignedBeaconBlock.OfFulu(fuluAnchor?.Signed ?? AnchorBlock),
            gloasAnchor?.Root ?? fuluAnchor?.Root ?? AnchorRoot,
            snapshots,
            failedBlocks: failedBlocks);
    }

    public BeaconChainStore CreateStore() => new(new MemColumnsDb<BeaconChainDbColumns>(), Spec);

    internal sealed class EnvelopeEngine : IEngineDriver
    {
        public ExecutionStatus EnvelopeVerdict { get; set; } = ExecutionStatus.Valid;

        public int EnvelopeCalls { get; private set; }

        public List<(Hash256 Head, Hash256 Safe, Hash256 Finalized)> FcuCalls { get; } = [];

        public SignedBeaconBlock? CurrentBlock { get; set; }

        public bool HasAnsweredNewPayload { get; private set; }

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash)
        {
            FcuCalls.Add((headExecHash, safeExecHash, finalizedExecHash));
            return Task.FromResult(PayloadStatusV1.Syncing);
        }

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body)
        {
            HasAnsweredNewPayload = true;
            return ExecutionStatus.Valid;
        }

        public ExecutionStatus NotifyNewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests)
        {
            EnvelopeCalls++;
            return EnvelopeVerdict;
        }
    }
}
