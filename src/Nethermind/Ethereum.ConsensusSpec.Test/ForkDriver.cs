// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

public abstract class ForkDriver
{
    public static readonly IReadOnlyDictionary<string, ForkDriver> ByName = new Dictionary<string, ForkDriver>(StringComparer.Ordinal)
    {
        ["electra"] = new Electra(),
        ["fulu"] = new Fulu(),
        ["gloas"] = new Gloas(),
    };

    public abstract string Fork { get; }

    private sealed class Fulu : ForkDriver<BeaconStateFulu>
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

        public override EpochCache NewCache() => new() { Hasher = new DifferentialBeaconStateHasher() };

        public override Hash256 CachedRoot(BeaconStateFulu state, EpochCache cache) => cache.Hasher.HashTreeRoot(state);

        public override ulong SlotOf(BeaconStateFulu state) => state.Slot;

        public override Validator[] ValidatorsOf(BeaconStateFulu state) => state.Validators!;

        public override void ProcessSlots(BeaconStateFulu state, ulong targetSlot, EpochCache cache) =>
            SlotProcessing.ProcessSlots(state, targetSlot, cache);

        public override void ApplyBlock(BeaconStateFulu state, byte[] signedBlockSsz, BeaconChainSpec spec, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, bool verifySignatures)
        {
            SignedBeaconBlock.Decode(signedBlockSsz, out SignedBeaconBlock signedBlock);
            StateTransition.Apply(state, signedBlock, cache, pubkeys, notifier, spec, validateResult: true, verifySignatures);
        }
    }

    /// <summary>Runs Electra using a Fulu working state, with Electra proposer sampling, epoch processing, blob limits and SSZ roots.</summary>
    /// <remarks>
    /// Refill lookahead at each epoch boundary: Electra samples against current balances, unlike Fulu's advance sampling.
    /// Skip Fulu's process_proposer_lookahead, which can read an empty future active set Electra never accesses.
    /// Hash the Electra base through EpochCache.Hasher and post-state comparison, excluding lookahead from roots.
    /// </remarks>
    private sealed class Electra : ForkDriver<BeaconStateFulu>
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

        public override ulong SlotOf(BeaconStateFulu state) => state.Slot;

        public override Validator[] ValidatorsOf(BeaconStateFulu state) => state.Validators!;

        public override void ProcessSlots(BeaconStateFulu state, ulong targetSlot, EpochCache cache)
        {
            // Same guard as SlotProcessing.ProcessSlots, which the loop below would otherwise skip.
            if (state.Slot >= targetSlot)
                throw new BeaconStateException($"Cannot advance state at slot {state.Slot} to non-future slot {targetSlot}");

            while (state.Slot < targetSlot)
            {
                SlotProcessing.ProcessSlot(state, cache.Hasher);
                if ((state.Slot + 1) % Presets.SlotsPerEpoch == 0)
                    ProcessEpoch(state, cache);
                state.Slot++;
                if (state.Slot % Presets.SlotsPerEpoch == 0)
                    RefillProposerLookahead(state);
            }
        }

        /// <summary>Electra <c>process_epoch</c>: <see cref="EpochProcessing.ProcessEpoch"/> without the Fulu-only <c>process_proposer_lookahead</c>.</summary>
        private static void ProcessEpoch(BeaconStateFulu state, EpochCache cache)
        {
            EpochProcessing.ProcessJustificationAndFinalization(state, cache);
            EpochProcessing.ProcessInactivityUpdates(state);
            EpochProcessing.ProcessRewardsAndPenalties(state, cache);
            EpochProcessing.ProcessRegistryUpdates(state, cache);
            EpochProcessing.ProcessSlashings(state, cache);
            EpochProcessing.ProcessEth1DataReset(state);
            EpochProcessing.ProcessPendingDeposits(state, cache);
            EpochProcessing.ProcessPendingConsolidations(state);
            EpochProcessing.ProcessEffectiveBalanceUpdates(state, cache);
            EpochProcessing.ProcessSlashingsReset(state);
            EpochProcessing.ProcessRandaoMixesReset(state);
            EpochProcessing.ProcessHistoricalSummariesUpdate(state);
            EpochProcessing.ProcessParticipationFlagUpdates(state);
            EpochProcessing.ProcessSyncCommitteeUpdates(state);
        }

        // Mirrors StateTransition.Apply statement for statement; that method cannot be reused because
        // its slot advance has no seam for the per-epoch lookahead refill and its root check is Fulu-shaped.
        public override void ApplyBlock(BeaconStateFulu state, byte[] signedBlockSsz, BeaconChainSpec spec, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, bool verifySignatures)
        {
            SignedBeaconBlock.Decode(signedBlockSsz, out SignedBeaconBlock signedBlock);
            BeaconBlock block = signedBlock.Message!;
            ProcessSlots(state, block.Slot, cache);

            if (verifySignatures && !SignatureSets.VerifyProposerSignature(state, signedBlock, pubkeys))
                throw new ProposerSignatureException($"Invalid proposer signature for the block at slot {block.Slot}");

            BlockProcessing.ProcessBlock(state, block, cache, pubkeys, notifier, spec.MaxBlobsPerBlockElectra, verifySignatures);

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

    private sealed class Gloas : ForkDriver<BeaconStateGloas>
    {
        public override string Fork => "gloas";

        public override BeaconStateGloas DecodePre(string path) => DecodeGloas(path);

        public override (object State, Hash256 Root) DecodePost(string path)
        {
            BeaconStateGloas post = DecodeGloas(path);
            return (post, SszRoots.HashTreeRoot(post));
        }

        public override Hash256 StateRoot(BeaconStateGloas state) => SszRoots.HashTreeRoot(state);

        // Re-decoded from its own encoding so the diff compares serialized values rather than a null against its zero default.
        public override object ForDiff(BeaconStateGloas state)
        {
            BeaconStateGloas.Decode(BeaconStateGloas.Encode(state), out BeaconStateGloas roundTripped);
            return roundTripped;
        }

        public override EpochCache NewCache() => new() { Hasher = new DifferentialBeaconStateHasher() };

        public override Hash256 CachedRoot(BeaconStateGloas state, EpochCache cache) => cache.Hasher.HashTreeRoot(state);

        public override ulong SlotOf(BeaconStateGloas state) => state.Slot;

        public override Validator[] ValidatorsOf(BeaconStateGloas state) => state.Validators!;

        public override void ProcessSlots(BeaconStateGloas state, ulong targetSlot, EpochCache cache) =>
            GloasSlotProcessing.ProcessSlots(state, targetSlot, cache);

        // Spec state_transition statement for statement, so a same-slot block fails process_slots as the spec asserts.
        public override void ApplyBlock(BeaconStateGloas state, byte[] signedBlockSsz, BeaconChainSpec spec, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, bool verifySignatures)
        {
            SignedBeaconBlockGloas.Decode(signedBlockSsz, out SignedBeaconBlockGloas signedBlock);
            BeaconBlockGloas block = signedBlock.Message!;
            ProcessSlots(state, block.Slot, cache);

            if (verifySignatures && !GloasBlockProcessing.VerifyProposerSignature(state, signedBlock, pubkeys))
                throw new ProposerSignatureException($"Invalid proposer signature for the block at slot {block.Slot}");

            GloasBlockProcessing.ProcessBlock(state, block, cache, pubkeys, notifier, spec, verifySignatures);

            if (block.StateRoot != CachedRoot(state, cache))
                throw new BeaconStateException($"Block state root {block.StateRoot} does not match the post-state root");
        }

        private static BeaconStateGloas DecodeGloas(string path)
        {
            BeaconStateGloas.Decode(SszConsensusTestLoader.ReadSszSnappy(path), out BeaconStateGloas state);
            return state;
        }
    }

    private static readonly PropertyInfo[] ElectraFields = typeof(BeaconStateElectra).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    internal static void CopyElectraFields(BeaconStateElectra from, BeaconStateElectra to)
    {
        foreach (PropertyInfo field in ElectraFields)
            field.SetValue(to, field.GetValue(from));
    }

    /// <summary>Hashes only Electra-declared fields, excluding the Fulu working state's lookahead.</summary>
    internal static Hash256 ElectraRoot(BeaconStateElectra state)
    {
        BeaconStateElectra.Merkleize(state, out UInt256 root);
        return new Hash256(root.ToLittleEndian());
    }

    /// <summary>Uses an out-of-range proposer sentinel for an empty active epoch, deferring rejection until Electra actually reads a proposer.</summary>
    internal const ulong NoProposer = ulong.MaxValue;

    internal static void RefillProposerLookahead(BeaconStateFulu state)
    {
        ulong epoch = state.GetCurrentEpoch();
        ulong[] lookahead = new ulong[Presets.ProposerLookaheadSlots];
        for (ulong i = 0; i <= Presets.MinSeedLookahead; i++)
        {
            int offset = (int)(i * Presets.SlotsPerEpoch);
            if (state.GetActiveValidatorIndices(epoch + i).Length == 0)
                Array.Fill(lookahead, NoProposer, offset, (int)Presets.SlotsPerEpoch);
            else
                state.ComputeProposerIndices(epoch + i).CopyTo(lookahead, offset);
        }
        state.ProposerLookahead = lookahead;
    }
}

public abstract class ForkDriver<TState> : ForkDriver where TState : class
{
    public abstract TState DecodePre(string path);

    public abstract (object State, Hash256 Root) DecodePost(string path);

    public abstract Hash256 StateRoot(TState state);

    public abstract object ForDiff(TState state);

    public abstract EpochCache NewCache();

    public virtual Hash256 CachedRoot(TState state, EpochCache cache) => StateRoot(state);

    public abstract ulong SlotOf(TState state);

    public abstract Validator[] ValidatorsOf(TState state);

    public abstract void ProcessSlots(TState state, ulong targetSlot, EpochCache cache);

    public abstract void ApplyBlock(TState state, byte[] signedBlockSsz, BeaconChainSpec spec, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, bool verifySignatures);
}
