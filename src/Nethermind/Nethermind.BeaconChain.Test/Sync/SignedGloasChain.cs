// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
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

/// <summary>
/// Genuinely signed self-built Gloas blocks on the <see cref="ForkCrossingChain"/> Fulu anchor (fork at epoch 1),
/// each with its own signed envelope, so the importer can run them with signature verification on. A block
/// builds full on its parent (its bid's <c>parent_block_hash</c> is the parent bid's <c>block_hash</c>) or empty.
/// </summary>
internal sealed class SignedGloasChain
{
    public BeaconChainSpec Spec => ForkCrossingChain.Instance.Spec;

    /// <summary>A copy of the shared anchor state, so an importer that mutated it would show here and not in other tests.</summary>
    public BeaconStateFulu AnchorState { get; } = ForkCrossingChain.Instance.AnchorState.Clone();

    public SignedBeaconBlock AnchorBlock { get; } = new() { Message = ForkCrossingChain.Instance.AnchorBlock, Signature = default };

    public Hash256 AnchorRoot => ForkCrossingChain.Instance.AnchorRoot;

    /// <summary>A Gloas block with the frozen post-state it seals and the envelope that reveals its payload.</summary>
    public sealed record Block(SignedBeaconBlockGloas Signed, Hash256 Root, BeaconStateGloas PostState, SignedExecutionPayloadEnvelope Envelope)
    {
        public ForkedSignedBeaconBlock Forked => new ForkedSignedBeaconBlock.OfGloas(Signed);

        public ExecutionPayloadBid Bid => Signed.Message!.Body!.SignedExecutionPayloadBid!.Message!;
    }

    /// <summary>A signed Fulu block with the frozen post-state it seals.</summary>
    public sealed record FuluBlock(SignedBeaconBlock Signed, Hash256 Root, BeaconStateFulu PostState)
    {
        public ForkedSignedBeaconBlock Forked => new ForkedSignedBeaconBlock.OfFulu(Signed);
    }

    /// <summary>Builds the Fulu block at <paramref name="slot"/> (before the fork) on the anchor, with an empty payload that builds on the anchor's.</summary>
    public FuluBlock NextFulu(ulong slot)
    {
        BeaconStateFulu preState = AnchorState.Clone();
        SlotProcessing.ProcessSlots(preState, slot, new EpochCache());
        ulong epoch = preState.GetCurrentEpoch();
        BeaconBlock message = TestChain.CreateBlock(slot, SszRoots.HashTreeRoot(preState.LatestBlockHeader!)).Message!;
        message.ProposerIndex = (ulong)preState.GetBeaconProposerIndex();
        Bls.SecretKey proposerKey = ValidatorKey((int)message.ProposerIndex);
        BeaconBlockBody body = message.Body!;
        body.RandaoReveal = Sign(proposerKey, Domains.ComputeSigningRoot(EpochRoot(epoch), preState.GetDomain(DomainType.Randao, epoch)));
        body.SyncAggregate!.SyncCommitteeSignature = new BlsSignature(SignatureSets.G2PointAtInfinity);
        Nethermind.BeaconChain.Types.ExecutionPayload payload = body.ExecutionPayload!;
        payload.ParentHash = preState.LatestExecutionPayloadHeader!.BlockHash;
        payload.PrevRandao = preState.GetRandaoMix(epoch);
        payload.Timestamp = preState.GenesisTime + slot * Presets.SecondsPerSlot;
        payload.BlockHash = Hash(0xE0);
        Hash256 proposerDomain = preState.GetDomain(DomainType.BeaconProposer, epoch);

        BeaconStateFulu postState = AnchorState.Clone();
        FuluStateTransition.Apply(postState, new SignedBeaconBlock { Message = message }, new EpochCache(), new PubkeyCache(), new AcceptingNotifier(), Spec, validateResult: false, verifySignatures: false);
        message.StateRoot = SszRoots.HashTreeRoot(postState);
        Hash256 root = SszRoots.HashTreeRoot(message);
        return new FuluBlock(new SignedBeaconBlock { Message = message, Signature = Sign(proposerKey, Domains.ComputeSigningRoot(root, proposerDomain)) }, root, postState);
    }

    /// <summary>Builds the block at <paramref name="slot"/> on <paramref name="parent"/>, or on the Fulu anchor across the fork when it is <c>null</c>.</summary>
    public Block Next(Block? parent, ulong slot, bool full, byte blockHashFill, SszKzgCommitment[]? blobCommitments = null) =>
        parent is null
            ? Build(CrossFork(AnchorState), slot, full, blockHashFill, blobCommitments)
            : Build(parent.PostState.Clone(), slot, full, blockHashFill, blobCommitments);

    /// <summary>Builds the first Gloas block at <paramref name="slot"/> on the Fulu block <paramref name="parent"/>, across the fork.</summary>
    public Block NextOnFulu(FuluBlock parent, ulong slot, bool full, byte blockHashFill) => Build(CrossFork(parent.PostState), slot, full, blockHashFill, null);

    private BeaconStateGloas CrossFork(BeaconStateFulu fuluParent)
    {
        BeaconStateFulu fulu = fuluParent.Clone();
        SlotProcessing.ProcessSlots(fulu, BoundarySlot, new EpochCache());
        return GloasForkTransition.UpgradeToGloas(fulu, Spec);
    }

    private Block Build(BeaconStateGloas state, ulong slot, bool full, byte blockHashFill, SszKzgCommitment[]? blobCommitments)
    {
        EpochCache cache = new();
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
        Bls.SecretKey proposerKey = ValidatorKey((int)message.ProposerIndex);
        ulong epoch = state.GetCurrentEpoch();
        message.Body!.RandaoReveal = Sign(proposerKey, Domains.ComputeSigningRoot(EpochRoot(epoch), state.GetDomain(DomainType.Randao, epoch)));
        Hash256 proposerDomain = state.GetDomain(DomainType.BeaconProposer, epoch);

        GloasBlockProcessing.ProcessBlock(state, message, cache, new PubkeyCache(), new AcceptingNotifier(), Spec, verifySignatures: false);
        message.StateRoot = SszRoots.HashTreeRoot(state);
        Hash256 root = SszRoots.HashTreeRoot(message);
        block.Signature = Sign(proposerKey, Domains.ComputeSigningRoot(root, proposerDomain));

        return new Block(block, root, state, ValidEnvelope(state, bid.Message!, proposerKey, Presets.BuilderIndexSelfBuild));
    }

    /// <summary>A real importer on the anchor whose store can hold Gloas blocks.</summary>
    public BlockImporter CreateImporter(IEngineDriver engine, System.Func<Hash256, ExecutionPayloadBid, bool>? isEnvelopeDataAvailable = null, ForkChoiceSnapshotHolder? snapshots = null, BeaconChainStore? store = null, ILogManager? logManager = null, SlotClock? clock = null)
    {
        PubkeyCache pubkeys = new();
        pubkeys.Build(AnchorState.Validators!);
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
            AnchorState,
            AnchorBlock,
            AnchorRoot,
            snapshots);
    }

    public BeaconChainStore CreateStore() => new(new MemColumnsDb<BeaconChainDbColumns>(), Spec);

    private static Hash256 EpochRoot(ulong epoch)
    {
        byte[] root = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(root, epoch);
        return new Hash256(root);
    }

    /// <summary>Answers every block body VALID and every envelope with <see cref="EnvelopeVerdict"/>, counting the envelope calls.</summary>
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
