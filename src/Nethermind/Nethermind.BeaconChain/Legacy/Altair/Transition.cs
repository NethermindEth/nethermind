// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Crypto;
using Body = Nethermind.BeaconChain.Legacy.Deneb.BeaconBlockBody;
using Phase0State = Nethermind.BeaconChain.Legacy.Phase0.BeaconState;

namespace Nethermind.BeaconChain.Legacy.Altair;

/// <summary>The Altair state transition: participation flags, inactivity scores and sync committees.</summary>
internal class Transition(BeaconChainSpec spec) : Phase0.Transition(spec)
{
    public override LegacyFork Fork => LegacyFork.Altair;
    protected override ulong MinSlashingPenaltyQuotient => LegacyPresets.MinSlashingPenaltyQuotientAltair;
    protected override ulong ProportionalSlashingMultiplier => LegacyPresets.ProportionalSlashingMultiplierAltair;

    public override LegacyBlock DecodeBlock(ReadOnlySpan<byte> ssz)
    {
        SignedBeaconBlock.Decode(ssz, out SignedBeaconBlock block);
        return new LegacyBlock(block, SszRoots.HashTreeRoot(block.Message!.Body!));
    }

    protected override BeaconStateFulu DecodeStateContainer(ReadOnlySpan<byte> ssz)
    {
        BeaconState.Decode(ssz, out BeaconState state);
        return state;
    }

    protected override void UpgradeFields(BeaconStateFulu state, EpochCache cache)
    {
        int validatorCount = state.ReadOnlyValidators.Length;
        state.PreviousEpochParticipation = new byte[validatorCount];
        state.CurrentEpochParticipation = new byte[validatorCount];
        state.InactivityScores = new ulong[validatorCount];
        foreach (Phase0.PendingAttestation attestation in ((Phase0State)state).PreviousEpochParticipation ?? [])
        {
            AttestationData data = attestation.Data!;
            byte flags = BlockProcessing.GetAttestationParticipationFlagIndices(state, data, attestation.InclusionDelay);
            foreach (ulong index in GetAttestingIndices(state, data, attestation.AggregationBits!, cache))
                state.PreviousEpochParticipation[index] |= flags;
        }
        state.CurrentSyncCommittee = state.NextSyncCommittee = GetNextSyncCommittee(state);
    }

    public override void ProcessBlock(BeaconStateFulu state, LegacyBlock block, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, bool verifySignatures, BlockSignatureBatch? batch)
    {
        base.ProcessBlock(state, block, cache, pubkeys, notifier, verifySignatures, batch);
        BlockProcessing.ProcessSyncAggregate(state, ((Body)block.Message.Body!).SyncAggregate!, cache, pubkeys, verifySignatures, batch);
    }

    public override void ProcessAttestation(BeaconStateFulu state, Attestation attestation, EpochCache cache, PubkeyCache pubkeys, bool verifySignature, BlockSignatureBatch? batch)
    {
        AttestationData data = attestation.Data!;
        ulong[] attestingIndices = ValidateAttestation(state, attestation, cache, pubkeys, verifySignature, batch);
        // The inclusion window before Deneb already implies the timely target condition EIP-7045 dropped.
        byte flags = BlockProcessing.GetAttestationParticipationFlagIndices(state, data, state.Slot - data.Slot);
        byte[] participation = data.Target!.Epoch == state.GetCurrentEpoch() ? state.CurrentEpochParticipation! : state.PreviousEpochParticipation!;

        ulong baseRewardPerIncrement = state.GetBaseRewardPerIncrement(cache);
        ulong proposerRewardNumerator = 0;
        foreach (ulong index in attestingIndices)
        {
            int newFlags = flags & ~participation[index];
            if (newFlags == 0)
                continue;
            participation[index] |= flags;
            ulong baseReward = state.ReadOnlyValidators[(int)index].EffectiveBalance / Presets.EffectiveBalanceIncrement * baseRewardPerIncrement;
            for (int flagIndex = 0; flagIndex < Presets.ParticipationFlagWeights.Length; flagIndex++)
            {
                if ((newFlags & (1 << flagIndex)) != 0)
                    proposerRewardNumerator += baseReward * Presets.ParticipationFlagWeights[flagIndex];
            }
        }

        const ulong proposerRewardDenominator = (Presets.WeightDenominator - Presets.ProposerWeight) * Presets.WeightDenominator / Presets.ProposerWeight;
        state.IncreaseBalance((int)state.GetBeaconProposerIndex(), proposerRewardNumerator / proposerRewardDenominator);
    }

    protected override void AddValidators(BeaconStateFulu state, List<Validator> validators, List<ulong> balances)
    {
        base.AddValidators(state, validators, balances);
        state.PreviousEpochParticipation = [.. state.PreviousEpochParticipation!, .. new byte[validators.Count]];
        state.CurrentEpochParticipation = [.. state.CurrentEpochParticipation!, .. new byte[validators.Count]];
        state.InactivityScores = [.. state.InactivityScores!, .. new ulong[validators.Count]];
    }

    public override void ProcessEpoch(BeaconStateFulu state, EpochCache cache)
    {
        ProcessJustificationAndFinalization(state, cache);
        EpochProcessing.ProcessInactivityUpdates(state);
        ProcessRewardsAndPenalties(state, cache);
        ProcessRegistryUpdates(state, cache);
        ProcessSlashings(state, cache);
        EpochProcessing.ProcessEth1DataReset(state);
        ProcessEffectiveBalanceUpdates(state, cache);
        EpochProcessing.ProcessSlashingsReset(state);
        EpochProcessing.ProcessRandaoMixesReset(state);
        ProcessHistoricalUpdate(state);
        ProcessParticipationUpdates(state);
        ProcessSyncCommitteeUpdates(state);
    }

    public override void ProcessJustificationAndFinalization(BeaconStateFulu state, EpochCache cache) =>
        EpochProcessing.ProcessJustificationAndFinalization(state, cache);

    public override void ProcessParticipationUpdates(BeaconStateFulu state) =>
        EpochProcessing.ProcessParticipationFlagUpdates(state);

    /// <remarks>The deltas are applied in specification order (source, target, head, inactivity), as each saturates at zero.</remarks>
    public override void ApplyRewardDeltas(BeaconStateFulu state, EpochCache cache)
    {
        ulong previousEpoch = state.GetPreviousEpoch();
        ulong baseRewardPerIncrement = state.GetBaseRewardPerIncrement(cache);
        ulong activeIncrements = state.GetTotalActiveBalance(cache) / Presets.EffectiveBalanceIncrement;
        bool isLeaking = previousEpoch - state.FinalizedCheckpoint!.Epoch > Presets.MinEpochsToInactivityPenalty;
        ReadOnlySpan<Validator> validators = state.ReadOnlyValidators;
        byte[] participation = state.PreviousEpochParticipation!;

        Span<ulong> participatingBalances = stackalloc ulong[Presets.ParticipationFlagWeights.Length];
        for (int i = 0; i < validators.Length; i++)
        {
            if (validators[i].Slashed || !validators[i].IsActiveValidator(previousEpoch))
                continue;
            for (int flagIndex = 0; flagIndex < participatingBalances.Length; flagIndex++)
            {
                if (BeaconStateAccessors.HasParticipationFlag(participation[i], flagIndex))
                    participatingBalances[flagIndex] += validators[i].EffectiveBalance;
            }
        }

        for (int i = 0; i < validators.Length; i++)
        {
            Validator validator = validators[i];
            bool isActive = validator.IsActiveValidator(previousEpoch);
            if (!isActive && !(validator.Slashed && previousEpoch + 1 < validator.WithdrawableEpoch))
                continue;

            byte flags = isActive && !validator.Slashed ? participation[i] : (byte)0;
            ulong baseReward = validator.EffectiveBalance / Presets.EffectiveBalanceIncrement * baseRewardPerIncrement;
            for (int flagIndex = 0; flagIndex < participatingBalances.Length; flagIndex++)
            {
                ulong weight = Presets.ParticipationFlagWeights[flagIndex];
                if (!BeaconStateAccessors.HasParticipationFlag(flags, flagIndex))
                {
                    if (flagIndex != Presets.TimelyHeadFlagIndex)
                        state.DecreaseBalance(i, baseReward * weight / Presets.WeightDenominator);
                }
                else if (!isLeaking)
                {
                    ulong participatingIncrements = Math.Max(Presets.EffectiveBalanceIncrement, participatingBalances[flagIndex]) / Presets.EffectiveBalanceIncrement;
                    state.IncreaseBalance(i, baseReward * weight * participatingIncrements / (activeIncrements * Presets.WeightDenominator));
                }
            }
            if (!BeaconStateAccessors.HasParticipationFlag(flags, Presets.TimelyTargetFlagIndex))
                state.DecreaseBalance(i, validator.EffectiveBalance * state.InactivityScores![i] / (Presets.InactivityScoreBias * LegacyPresets.InactivityPenaltyQuotientAltair));
        }
    }

    /// <summary>Spec <c>process_sync_committee_updates</c>.</summary>
    public virtual void ProcessSyncCommitteeUpdates(BeaconStateFulu state)
    {
        if ((state.GetCurrentEpoch() + 1) % Presets.EpochsPerSyncCommitteePeriod != 0)
            return;
        state.CurrentSyncCommittee = state.NextSyncCommittee;
        state.NextSyncCommittee = GetNextSyncCommittee(state);
    }

    private static SyncCommittee GetNextSyncCommittee(BeaconStateFulu state)
    {
        ulong epoch = state.GetCurrentEpoch() + 1;
        int[] indices = SampleByEffectiveBalance(state, state.GetActiveValidatorIndices(epoch), state.GetSeed(epoch, DomainType.SyncCommittee).Bytes, Presets.SyncCommitteeSize);
        BlsPublicKey[] pubkeys = new BlsPublicKey[indices.Length];
        BlsSigner.AggregatedPublicKey aggregate = new();
        Bls.P1Affine publicKey = new(stackalloc long[Bls.P1Affine.Sz]);
        for (int i = 0; i < indices.Length; i++)
        {
            pubkeys[i] = state.ReadOnlyValidators[indices[i]].Pubkey;
            if (!BlsSignatureSet.TryKeyValidate(pubkeys[i].Bytes, publicKey))
                throw new BeaconStateException($"Invalid sync committee pubkey for validator {indices[i]}");
            aggregate.Aggregate(publicKey);
        }
        return new SyncCommittee { Pubkeys = pubkeys, AggregatePubkey = new BlsPublicKey(aggregate.PublicKey.Compress()) };
    }
}
