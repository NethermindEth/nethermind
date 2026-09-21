// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Per-fork plumbing for the suites that drive this repo's state transition (operations,
/// epoch_processing, sanity). The pipeline itself is typed to <see cref="BeaconStateFulu"/>, so a fork
/// is driven only when its state can be carried through that pipeline under the fork's own semantics
/// and compared by the fork's own state root; anything less would be a false pass. Fulu is the
/// pipeline's native fork. Electra's state transition differs from Fulu's in exactly two places -
/// EIP-7917's <c>get_beacon_proposer_index</c> reads <c>proposer_lookahead</c> instead of sampling on
/// the fly, and <c>process_epoch</c> ends with <c>process_proposer_lookahead</c> - plus the state's SSZ
/// shape, and <see cref="Electra"/> accounts for each. Forks before Electra have no state container in
/// this repo and are not driven (see <see cref="ConsensusSpecArchive.StateTransitionForks"/>).
/// </summary>
public abstract class ForkDriver
{
    public static readonly IReadOnlyDictionary<string, ForkDriver> ByName = new Dictionary<string, ForkDriver>(StringComparer.Ordinal)
    {
        ["electra"] = new Electra(),
        ["fulu"] = new Fulu(),
    };

    public abstract string Fork { get; }

    /// <summary>Decodes a pre-state in the fork's SSZ shape into the pipeline's working type.</summary>
    public abstract BeaconStateFulu DecodePre(string path);

    /// <summary>Decodes an expected post-state in the fork's SSZ shape, with its root, for comparison against the working state.</summary>
    public abstract (object State, Hash256 Root) DecodePost(string path);

    /// <summary><c>hash_tree_root</c> of the working state in the fork's SSZ shape.</summary>
    public abstract Hash256 StateRoot(BeaconStateFulu state);

    /// <summary>The working state as the fork's own container, so <see cref="FuluDriverSupport.Diff"/> can walk it beside <see cref="DecodePost"/>'s result.</summary>
    public abstract object ForDiff(BeaconStateFulu state);

    /// <summary>A cache whose hasher writes the fork's own state root into <c>state_roots</c> and the latest block header.</summary>
    public abstract EpochCache NewCache();

    /// <summary><c>process_slots</c> under the fork's semantics.</summary>
    public abstract void ProcessSlots(BeaconStateFulu state, ulong targetSlot, EpochCache cache);

    /// <summary><c>state_transition</c> under the fork's semantics, validating the block's claimed state root in the fork's shape.</summary>
    public abstract void ApplyBlock(BeaconStateFulu state, SignedBeaconBlock signedBlock, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, bool verifySignatures);

    private sealed class Fulu : ForkDriver
    {
        public override string Fork => "fulu";

        public override BeaconStateFulu DecodePre(string path) => FuluDriverSupport.DecodeState(path);

        public override (object State, Hash256 Root) DecodePost(string path)
        {
            BeaconStateFulu post = FuluDriverSupport.DecodeState(path);
            return (post, FuluDriverSupport.StateRoot(post));
        }

        public override Hash256 StateRoot(BeaconStateFulu state) => FuluDriverSupport.StateRoot(state);

        public override object ForDiff(BeaconStateFulu state) => state;

        public override EpochCache NewCache() => new();

        public override void ProcessSlots(BeaconStateFulu state, ulong targetSlot, EpochCache cache) =>
            SlotProcessing.ProcessSlots(state, targetSlot, cache);

        public override void ApplyBlock(BeaconStateFulu state, SignedBeaconBlock signedBlock, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, bool verifySignatures) =>
            StateTransition.Apply(state, signedBlock, cache, pubkeys, notifier, BeaconChainSpec.Mainnet, validateResult: true, verifySignatures);
    }

    /// <summary>
    /// Carries an Electra state through the Fulu pipeline as a <see cref="BeaconStateFulu"/> whose
    /// <c>proposer_lookahead</c> holds what Electra's on-the-fly <c>get_beacon_proposer_index</c> would
    /// answer, and takes every state root in the Electra shape.
    /// </summary>
    /// <remarks>
    /// The lookahead is filled with <c>compute_proposer_indices</c> for the current epoch (Fulu's
    /// per-slot sampling is Electra's, seed and balance weighting included) and refilled at every epoch
    /// start. Within an epoch Electra's answer is fixed - the active set, the effective balances and the
    /// seed's RANDAO mix only move at epoch processing - so the refill reproduces it exactly, whereas
    /// Fulu's own lookahead for the next epoch is sampled an epoch early against older effective
    /// balances and can name a different proposer. The next-epoch half is filled too but nothing in the
    /// state transition reads it. Roots are merkleized as the working state's Electra base (37 fields),
    /// both per slot via <see cref="EpochCache.Hasher"/> and for the post-state comparison, so the
    /// lookahead never leaks into a root an Electra vector compares.
    /// </remarks>
    private sealed class Electra : ForkDriver
    {
        public override string Fork => "electra";

        public override BeaconStateFulu DecodePre(string path)
        {
            BeaconStateFulu state = new();
            CopyElectraFields(DecodeElectra(path), state);
            RefillProposerLookahead(state);
            return state;
        }

        public override (object State, Hash256 Root) DecodePost(string path)
        {
            BeaconStateElectra post = DecodeElectra(path);
            return (post, ElectraRoot(post));
        }

        public override Hash256 StateRoot(BeaconStateFulu state) => ElectraRoot(state);

        public override object ForDiff(BeaconStateFulu state)
        {
            BeaconStateElectra electra = new();
            CopyElectraFields(state, electra);
            return electra;
        }

        public override EpochCache NewCache() => new() { Hasher = new ElectraShapeHasher() };

        public override void ProcessSlots(BeaconStateFulu state, ulong targetSlot, EpochCache cache)
        {
            // Same guard as SlotProcessing.ProcessSlots, which the loop below would otherwise skip.
            if (state.Slot >= targetSlot)
                throw new BeaconStateException($"Cannot advance state at slot {state.Slot} to non-future slot {targetSlot}");

            while (state.Slot < targetSlot)
            {
                ulong nextEpochStart = BeaconStateAccessors.ComputeStartSlotAtEpoch(state.GetCurrentEpoch() + 1);
                SlotProcessing.ProcessSlots(state, Math.Min(targetSlot, nextEpochStart), cache);
                if (state.Slot == nextEpochStart)
                    RefillProposerLookahead(state);
            }
        }

        // Mirrors StateTransition.Apply statement for statement; that method cannot be reused because
        // its slot advance has no seam for the per-epoch lookahead refill and its root check is Fulu-shaped.
        public override void ApplyBlock(BeaconStateFulu state, SignedBeaconBlock signedBlock, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, bool verifySignatures)
        {
            BeaconBlock block = signedBlock.Message!;
            ProcessSlots(state, block.Slot, cache);

            if (verifySignatures && !SignatureSets.VerifyProposerSignature(state, signedBlock, pubkeys))
                throw new BeaconStateException($"Invalid proposer signature for the block at slot {block.Slot}");

            BlockProcessing.ProcessBlock(state, block, cache, pubkeys, notifier, FuluDriverSupport.MaxBlobsPerBlockElectra, verifySignatures);

            if (block.StateRoot != StateRoot(state))
                throw new BeaconStateException($"Block state root {block.StateRoot} does not match the post-state root");
        }

        private static BeaconStateElectra DecodeElectra(string path)
        {
            BeaconStateElectra.Decode(SszConsensusTestLoader.ReadSszSnappy(path), out BeaconStateElectra state);
            return state;
        }

        private sealed class ElectraShapeHasher : IBeaconStateHasher
        {
            public Hash256 HashTreeRoot(BeaconStateFulu state) => ElectraRoot(state);
        }
    }

    private static readonly PropertyInfo[] ElectraFields = typeof(BeaconStateElectra).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    /// <summary>Copies every Electra-declared field by reference; the Fulu-only <c>proposer_lookahead</c> is untouched.</summary>
    internal static void CopyElectraFields(BeaconStateElectra from, BeaconStateElectra to)
    {
        foreach (PropertyInfo field in ElectraFields)
            field.SetValue(to, field.GetValue(from));
    }

    /// <summary>
    /// The generated merkleization of the Electra container reads only Electra-declared fields, so a
    /// <see cref="BeaconStateFulu"/> passed as its base hashes to the root an Electra node would compute.
    /// </summary>
    internal static Hash256 ElectraRoot(BeaconStateElectra state)
    {
        BeaconStateElectra.Merkleize(state, out UInt256 root);
        return new Hash256(root.ToLittleEndian());
    }

    /// <summary>Fills the whole lookahead from the current state, current epoch first: what Electra samples on the fly for the current epoch.</summary>
    internal static void RefillProposerLookahead(BeaconStateFulu state)
    {
        ulong epoch = state.GetCurrentEpoch();
        ulong[] lookahead = new ulong[Presets.ProposerLookaheadSlots];
        for (ulong i = 0; i <= Presets.MinSeedLookahead; i++)
            state.ComputeProposerIndices(epoch + i).CopyTo(lookahead, (int)(i * Presets.SlotsPerEpoch));
        state.ProposerLookahead = lookahead;
    }
}
