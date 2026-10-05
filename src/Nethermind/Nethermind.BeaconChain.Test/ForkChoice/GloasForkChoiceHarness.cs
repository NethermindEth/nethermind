// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>Built post-states skip process_block; fork choice reads only slot, checkpoints, registry and PTC window.</summary>
internal sealed class GloasForkChoiceHarness : IForkChoiceStateProvider, IGloasBlockStateProvider
{
    public const ulong CommitteeWeight = 64 * 32 * Gwei;

    private readonly Dictionary<Hash256, BeaconStateGloas> _states = [];

    public GloasForkChoiceHarness(bool gloasAnchor = false)
    {
        PubkeyCache pubkeys = new();
        pubkeys.Build(Chain.AnchorState.Validators!);
        Runner = gloasAnchor
            ? new ForkChoiceRunner(Chain.Spec, Chain.First.PostState, Chain.First.Block.Message!, this, pubkeys, this)
            : new ForkChoiceRunner(Chain.Spec, Chain.AnchorState, Chain.AnchorBlock, this, pubkeys, this);
        First = new Block(Chain.First.Root, Chain.First.PostState, Chain.First.Block);
    }

    public sealed record Block(Hash256 Root, BeaconStateGloas PostState, SignedBeaconBlockGloas Signed)
    {
        public ulong Slot => Signed.Message!.Slot;

        public ulong ProposerIndex => Signed.Message!.ProposerIndex;

        public Hash256 BidBlockHash => Signed.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlockHash!;
    }

    public ForkCrossingChain Chain => ForkCrossingChain.Instance;

    public ForkChoiceRunner Runner { get; }

    public Block First { get; }

    public static ulong ProposerScore => (ulong)ValidatorCount * 32 * Gwei / Presets.SlotsPerEpoch * ProtoArrayForkChoice.DefaultProposerScoreBoostPercent / 100;

    public void TickTo(ulong slot, ulong secondsIntoSlot = 0) =>
        Runner.OnTick(Runner.GenesisTime + slot * Presets.SecondsPerSlot + secondsIntoSlot);

    public void Import(Block block) => Runner.OnBlock(block.Signed, block.PostState);

    public Block Child(Block parent, ulong slot, bool full, byte blockHashFill, params PayloadAttestation[] payloadAttestations) =>
        Child(parent, slot, full ? parent.BidBlockHash : parent.PostState.LatestBlockHash!, blockHashFill, payloadAttestations);

    /// <summary>An EMPTY child names the payload its parent built on (gloas/fork-choice.md get_parent_payload_status).</summary>
    public Block Child(Block parent, ulong slot, Hash256 parentBlockHash, byte blockHashFill, params PayloadAttestation[] payloadAttestations)
    {
        BeaconStateGloas state = parent.PostState.Clone();
        GloasSlotProcessing.ProcessSlots(state, slot, new EpochCache());
        SignedBeaconBlockGloas block = MinimalBlock(state, SelfBuildBid(state, parentBlockHash, Hash(blockHashFill)));
        // A built parent's state never ran its block, so its latest header is the grandparent's.
        block.Message!.ParentRoot = parent.Root;
        block.Message.Body!.PayloadAttestations = payloadAttestations;
        Hash256 root = SszRoots.HashTreeRoot(block.Message);
        _states[root] = state;
        return new Block(root, state, block);
    }

    public ulong[] Ptc(Block block) => block.PostState.GetPtc(block.Slot, Chain.Spec).Indices!;

    public PayloadAttestationMessage PtcMessage(Block block, ulong validatorIndex, bool payloadPresent, bool blobDataAvailable, bool sign, ulong? slot = null)
    {
        PayloadAttestationData data = new() { BeaconBlockRoot = block.Root, Slot = slot ?? block.Slot, PayloadPresent = payloadPresent, BlobDataAvailable = blobDataAvailable };
        Hash256 domain = block.PostState.GetDomain(DomainType.PtcAttester, BeaconStateAccessors.ComputeEpochAtSlot(data.Slot));
        return new PayloadAttestationMessage
        {
            ValidatorIndex = validatorIndex,
            Data = data,
            Signature = sign ? Sign(ValidatorKey((int)validatorIndex), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), domain)) : default,
        };
    }

    public void AllPtcVote(Block block, bool payloadPresent, bool blobDataAvailable)
    {
        Runner.GetHead();
        foreach (ulong member in Ptc(block).Distinct())
            Runner.OnPayloadAttestationMessage(PtcMessage(block, member, payloadPresent, blobDataAvailable, sign: false), verifySignature: false);
    }

    public void CommitteeVotes(Block block, ulong slot, ulong index)
    {
        ulong epoch = slot / Presets.SlotsPerEpoch;
        Hash256 target = Runner.EnumerateAncestors(block.Root).First(n => n.Slot <= epoch * Presets.SlotsPerEpoch).Root;
        AttestationData data = new()
        {
            Slot = slot,
            Index = index,
            BeaconBlockRoot = block.Root,
            Source = new Checkpoint { Epoch = 0, Root = Chain.AnchorRoot },
            Target = new Checkpoint { Epoch = epoch, Root = target },
        };
        AttestationGloas attestation = CommitteeAttestation(Chain.First.PostState, data, new EpochCache().GetCommitteeCache(Chain.First.PostState, epoch), 0, sign: false);
        Runner.OnAttestation(attestation, isFromBlock: true, verifySignature: false);
    }

    public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => Chain.GetBlockState(blockRoot);

    public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => Chain.CopyBlockState(blockRoot);

    public BeaconStateGloas? GetGloasBlockState(Hash256 blockRoot) => _states.GetValueOrDefault(blockRoot) ?? Chain.GetGloasBlockState(blockRoot);
}
