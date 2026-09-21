// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using FuluStateTransition = Nethermind.BeaconChain.StateTransition.StateTransition;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// Hand-built blocks over the <see cref="ImportableBlobBlock"/> anchor, carrying whatever body
/// attestations and attester slashings a test wants fork choice to see. Nothing is signed: every
/// consumer runs the transition and the fork-choice replay with signature verification off, the
/// way trusted store replays and the transition-verified body replay do. No mainnet fork-choice
/// vector carries a body attester slashing, so this is what exercises that replay path.
/// </summary>
internal sealed class UnsignedChain : IForkChoiceStateProvider
{
    private static readonly BlsSignature Unsigned = new(SignatureSets.G2PointAtInfinity);

    private readonly Dictionary<Hash256, BeaconStateFulu> _postStates = [];
    private readonly CommitteeCache _committees;

    private UnsignedChain(ImportableBlobBlock anchor)
    {
        Anchor = anchor;
        _postStates[anchor.AnchorRoot] = anchor.AnchorState;
        _committees = new EpochCache().GetCommitteeCache(anchor.AnchorState, 0);
    }

    /// <summary>A block of this chain with its real post-state, the root the state transition sealed it under.</summary>
    public sealed record ChainBlock(SignedBeaconBlock Block, Hash256 Root, BeaconStateFulu PostState);

    /// <inheritdoc cref="BuildEquivocation"/>
    public sealed record Equivocation(ChainBlock A, ChainBlock B, ChainBlock Voted, ChainBlock Slashing);

    /// <summary>The anchor, its state and the pubkey cache; the fixture's own signed block is not used.</summary>
    public ImportableBlobBlock Anchor { get; }

    public BeaconChainSpec Spec => Anchor.Spec;

    public Hash256 AnchorRoot => Anchor.AnchorRoot;

    public static UnsignedChain Create() => new(ImportableBlobBlock.CreateWithoutBlobs());

    public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => _postStates.GetValueOrDefault(blockRoot);

    public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();

    /// <summary>
    /// Builds and seals a block at <paramref name="slot"/> on <paramref name="parentRoot"/>, whose
    /// post-state this chain must hold. The body carries only what is passed in.
    /// </summary>
    /// <param name="payloadHashByte">Fills the execution block hash; distinct per block so no two blocks share a payload.</param>
    public ChainBlock Extend(Hash256 parentRoot, ulong slot, byte payloadHashByte, Attestation[]? attestations = null, AttesterSlashing[]? attesterSlashings = null)
    {
        BeaconStateFulu parentState = _postStates[parentRoot];
        BeaconBlock block = TestChain.CreateBlock(slot, parentRoot).Message!;
        // The anchor's proposer lookahead is all zeros, so validator 0 proposes every slot.
        block.ProposerIndex = 0;
        BeaconBlockBody body = block.Body!;
        body.RandaoReveal = Unsigned;
        body.Attestations = attestations ?? [];
        body.AttesterSlashings = attesterSlashings ?? [];
        ExecutionPayload payload = body.ExecutionPayload!;
        payload.ParentHash = parentState.LatestExecutionPayloadHeader!.BlockHash;
        payload.PrevRandao = parentState.GetRandaoMix(parentState.GetCurrentEpoch());
        payload.Timestamp = parentState.GenesisTime + slot * Presets.SecondsPerSlot;
        payload.BlockNumber = parentState.LatestExecutionPayloadHeader.BlockNumber + 1;
        payload.BlockHash = Hash(payloadHashByte);

        SignedBeaconBlock signedBlock = new() { Message = block, Signature = Unsigned };
        BeaconStateFulu postState = parentState.Clone();
        FuluStateTransition.Apply(postState, signedBlock, new EpochCache(), Anchor.Pubkeys, new AcceptingNotifier(), Spec, validateResult: false, verifySignatures: false);
        block.StateRoot = SszRoots.HashTreeRoot(postState);
        Hash256 root = SszRoots.HashTreeRoot(block);
        _postStates[root] = postState;
        return new ChainBlock(signedBlock, root, postState);
    }

    /// <summary>
    /// Two competing slot-1 blocks A and B, a slot-6 block on A whose body votes two committees onto
    /// A and one onto B, and a slot-7 block on that one whose body slashes A's two voters for their
    /// double vote. Built only; the caller imports them.
    /// </summary>
    public Equivocation BuildEquivocation()
    {
        ChainBlock a = Extend(AnchorRoot, slot: 1, payloadHashByte: 0xa1);
        ChainBlock b = Extend(AnchorRoot, slot: 1, payloadHashByte: 0xb1);
        // With this registry only odd slots have a (one-member) committee; the three are disjoint.
        ulong[] equivocators = [.. Committee(1), .. Committee(3)];
        Array.Sort(equivocators);
        ChainBlock voted = Extend(a.Root, slot: 6, payloadHashByte: 0xa6, attestations: [Vote(1, a.Root), Vote(3, a.Root), Vote(5, b.Root)]);
        ChainBlock slashing = Extend(voted.Root, slot: 7, payloadHashByte: 0xa7, attesterSlashings: [DoubleVote(equivocators, slot: 1, a.Root, b.Root)]);
        return new Equivocation(a, b, voted, slashing);
    }

    /// <summary>The validators in the single committee of <paramref name="slot"/> (epoch 0), ascending; empty at half the slots with this small registry.</summary>
    public ulong[] Committee(ulong slot)
    {
        ReadOnlySpan<int> members = _committees.GetBeaconCommittee(slot, 0);
        ulong[] indices = new ulong[members.Length];
        for (int i = 0; i < members.Length; i++)
        {
            indices[i] = (ulong)members[i];
        }

        Array.Sort(indices);
        return indices;
    }

    /// <summary>An unsigned aggregate of the whole committee of <paramref name="slot"/> voting for <paramref name="headRoot"/>, with the anchor as source and target.</summary>
    public Attestation Vote(ulong slot, Hash256 headRoot)
    {
        BitArray committeeBits = new(Presets.MaxCommitteesPerSlot);
        committeeBits[0] = true;
        BitArray aggregationBits = new(_committees.GetBeaconCommittee(slot, 0).Length, true);
        return new Attestation
        {
            AggregationBits = aggregationBits,
            Data = VoteData(slot, headRoot),
            Signature = Unsigned,
            CommitteeBits = committeeBits,
        };
    }

    /// <summary>A double vote by <paramref name="validators"/> (ascending) at <paramref name="slot"/>: two attestations for one target epoch that differ only in the head root.</summary>
    public AttesterSlashing DoubleVote(ulong[] validators, ulong slot, Hash256 headRoot1, Hash256 headRoot2) =>
        new()
        {
            Attestation1 = new IndexedAttestation { AttestingIndices = validators, Data = VoteData(slot, headRoot1), Signature = Unsigned },
            Attestation2 = new IndexedAttestation { AttestingIndices = validators, Data = VoteData(slot, headRoot2), Signature = Unsigned },
        };

    private AttestationData VoteData(ulong slot, Hash256 headRoot) =>
        new()
        {
            Slot = slot,
            Index = 0,
            BeaconBlockRoot = headRoot,
            Source = Anchor.AnchorState.CurrentJustifiedCheckpoint,
            Target = new Checkpoint { Epoch = 0, Root = AnchorRoot },
        };

    private static Hash256 Hash(byte value)
    {
        byte[] bytes = new byte[32];
        bytes.AsSpan().Fill(value);
        return new Hash256(bytes);
    }

    private sealed class AcceptingNotifier : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }
}
