// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Test.Engine;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using FuluStateTransition = Nethermind.BeaconChain.StateTransition.StateTransition;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>Body operations stay unsigned and require trusted replay, even when block/RANDAO signatures are enabled.</summary>
internal sealed class UnsignedChain : IForkChoiceStateProvider
{
    private static readonly BlsSignature Unsigned = new(SignatureSets.G2PointAtInfinity);

    private readonly Dictionary<Hash256, BeaconStateFulu> _postStates = [];
    private readonly CommitteeCache _committees;

    // The importer hashes incrementally, so a test of its roots passes a full hasher to compare against.
    private readonly IBeaconStateHasher _hasher;

    private UnsignedChain(ImportableBlobBlock anchor, IBeaconStateHasher hasher)
    {
        _hasher = hasher;
        Anchor = anchor;
        _postStates[anchor.AnchorRoot] = anchor.AnchorState;
        _committees = new EpochCache().GetCommitteeCache(anchor.AnchorState, 0);
    }

    public sealed record ChainBlock(SignedBeaconBlock Block, Hash256 Root, BeaconStateFulu PostState);

    public sealed record Equivocation(ChainBlock A, ChainBlock B, ChainBlock Voted, ChainBlock Slashing);

    public ImportableBlobBlock Anchor { get; }

    public BeaconChainSpec Spec => Anchor.Spec;

    public Hash256 AnchorRoot => Anchor.AnchorRoot;

    public static UnsignedChain Create(ImportableBlobBlock? anchor = null, IBeaconStateHasher? hasher = null) =>
        new(anchor ?? ImportableBlobBlock.CreateWithoutBlobs(), hasher ?? new CachedBeaconStateHasher());

    public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => _postStates.GetValueOrDefault(blockRoot);

    public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();

    public ChainBlock Extend(Hash256 parentRoot, ulong slot, byte payloadHashByte, Attestation[]? attestations = null, AttesterSlashing[]? attesterSlashings = null, bool signed = false)
    {
        BeaconStateFulu parentState = _postStates[parentRoot];
        BeaconBlock block = TestChain.CreateBlock(slot, parentRoot).Message!;
        BeaconStateFulu atSlot = parentState.Clone();
        SlotProcessing.ProcessSlots(atSlot, slot, new EpochCache { Hasher = _hasher });
        block.ProposerIndex = atSlot.GetBeaconProposerIndex();
        BeaconBlockBody body = block.Body!;
        ulong epoch = atSlot.GetCurrentEpoch();
        body.RandaoReveal = Unsigned;
        if (signed)
        {
            body.RandaoReveal = ImportableBlobBlock.SignAs(block.ProposerIndex, ImportableBlobBlock.EpochRoot(epoch), atSlot.GetDomain(DomainType.Randao, epoch));
            body.SyncAggregate!.SyncCommitteeSignature = Unsigned;
        }
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
        FuluStateTransition.Apply(postState, signedBlock, new EpochCache { Hasher = _hasher }, Anchor.Pubkeys, new TestEngineDriver.BodyOnlyNotifier(), Spec, validateResult: false, verifySignatures: false);
        block.StateRoot = _hasher.HashTreeRoot(postState);
        Hash256 root = SszRoots.HashTreeRoot(block);
        if (signed)
        {
            signedBlock = new SignedBeaconBlock { Message = block, Signature = ImportableBlobBlock.SignAs(block.ProposerIndex, root, atSlot.GetDomain(DomainType.BeaconProposer, epoch)) };
        }

        _postStates[root] = postState;
        return new ChainBlock(signedBlock, root, postState);
    }

    public Equivocation BuildEquivocation()
    {
        ChainBlock a = Extend(AnchorRoot, slot: 1, payloadHashByte: 0xa1);
        ChainBlock b = Extend(AnchorRoot, slot: 1, payloadHashByte: 0xb1);
        ulong[] equivocators = [.. Committee(1), .. Committee(3)];
        Array.Sort(equivocators);
        ChainBlock voted = Extend(a.Root, slot: 6, payloadHashByte: 0xa6, attestations: [Vote(1, a.Root), Vote(3, a.Root), Vote(5, b.Root)]);
        ChainBlock slashing = Extend(voted.Root, slot: 7, payloadHashByte: 0xa7, attesterSlashings: [DoubleVote(equivocators, slot: 1, a.Root, b.Root)]);
        return new Equivocation(a, b, voted, slashing);
    }

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

    private static Hash256 Hash(byte value) => new(Enumerable.Repeat(value, 32).ToArray());
}
