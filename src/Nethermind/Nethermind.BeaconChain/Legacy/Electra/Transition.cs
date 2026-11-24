// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.Legacy.Electra;

/// <summary>The Electra state transition: the current transition without Fulu's proposer lookahead.</summary>
internal sealed class Transition(BeaconChainSpec spec) : Deneb.Transition(spec)
{
    public override LegacyFork Fork => LegacyFork.Electra;

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
        ulong earliestEpoch = BeaconStateAccessors.ComputeActivationExitEpoch(state.GetCurrentEpoch());
        ulong earliestExitEpoch = earliestEpoch;
        Validator[] validators = state.Validators!;
        List<(ulong EligibilityEpoch, int Index)> preActivation = [];
        for (int i = 0; i < validators.Length; i++)
        {
            if (validators[i].ExitEpoch != Presets.FarFutureEpoch)
                earliestExitEpoch = Math.Max(earliestExitEpoch, validators[i].ExitEpoch);
            if (validators[i].ActivationEpoch == Presets.FarFutureEpoch)
                preActivation.Add((validators[i].ActivationEligibilityEpoch, i));
        }

        state.DepositRequestsStartIndex = Presets.UnsetDepositRequestsStartIndex;
        state.DepositBalanceToConsume = 0;
        state.EarliestExitEpoch = earliestExitEpoch + 1;
        state.EarliestConsolidationEpoch = earliestEpoch;
        state.PendingPartialWithdrawals = [];
        state.PendingConsolidations = [];
        state.ExitBalanceToConsume = state.GetActivationExitChurnLimit(cache);
        state.ConsolidationBalanceToConsume = state.GetConsolidationChurnLimit(cache);

        // Validators that are not yet active re-enter through the balance deposit queue.
        preActivation.Sort();
        PendingDeposit[] deposits = new PendingDeposit[preActivation.Count];
        for (int i = 0; i < deposits.Length; i++)
        {
            int index = preActivation[i].Index;
            Validator reset = validators[index].Clone();
            reset.EffectiveBalance = 0;
            reset.ActivationEligibilityEpoch = Presets.FarFutureEpoch;
            validators[index] = reset;
            deposits[i] = new PendingDeposit
            {
                Pubkey = reset.Pubkey,
                WithdrawalCredentials = reset.WithdrawalCredentials,
                Amount = state.Balances![index],
                // The G2 point at infinity is the signature placeholder, and GENESIS_SLOT
                // distinguishes this from a pending deposit request.
                Signature = new BlsSignature(SignatureSets.G2PointAtInfinity),
                Slot = Presets.GenesisSlot,
            };
            state.Balances[index] = 0;
        }
        state.PendingDeposits = deposits;

        // Early adopters of compounding credentials go through the activation churn.
        for (int i = 0; i < validators.Length; i++)
        {
            if (validators[i].HasCompoundingWithdrawalCredential())
                BlockProcessing.QueueExcessActiveBalance(state, i);
        }
    }

    /// <summary>Upgrades an Electra state in place to Fulu, at the first slot of Fulu's activation epoch.</summary>
    public void UpgradeToFulu(BeaconStateFulu state)
    {
        state.Fork = new Fork
        {
            PreviousVersion = state.Fork!.CurrentVersion,
            CurrentVersion = Spec.VersionForEpoch(Spec.FuluForkEpoch),
            Epoch = state.GetCurrentEpoch(),
        };
        RefillProposers(state);
    }

    public override void ProcessBlock(BeaconStateFulu state, LegacyBlock block, EpochCache cache, PubkeyCache pubkeys, INewPayloadNotifier notifier, bool verifySignatures, BlockSignatureBatch? batch) =>
        BlockProcessing.ProcessBlock(state, block.Message, cache, pubkeys, notifier, Spec.MaxBlobsPerBlockElectra, verifySignatures, batch);

    /// <remarks>Electra samples proposers against the balances of the epoch itself, so the second epoch is only right for <see cref="UpgradeToFulu"/>.</remarks>
    protected override void RefillProposers(BeaconStateFulu state)
    {
        ulong epoch = state.GetCurrentEpoch();
        ulong[] lookahead = new ulong[Presets.ProposerLookaheadSlots];
        Array.Fill(lookahead, ulong.MaxValue);
        for (ulong i = 0; i <= Presets.MinSeedLookahead; i++)
        {
            if (state.GetActiveValidatorIndices(epoch + i).Length > 0)
                state.ComputeProposerIndices(epoch + i).CopyTo(lookahead, (int)(i * Presets.SlotsPerEpoch));
        }
        state.ProposerLookahead = lookahead;
    }

    public override void ProcessEpoch(BeaconStateFulu state, EpochCache cache)
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
}
