// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.StateTransition;

public class StateTransitionFoundationTests
{
    private const ulong Gwei = 1_000_000_000;

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(33)]
    [TestCase(100)]
    [TestCase(1000)]
    [TestCase(6271)]
    public void Bulk_shuffle_matches_per_index_shuffle_and_round_trips(int count)
    {
        byte[] seed = SHA256.HashData(BitConverter.GetBytes(count));

        int[] perIndex = new int[count];
        for (int i = 0; i < count; i++)
        {
            perIndex[i] = SwapOrNotShuffle.ComputeShuffledIndex(i, count, seed);
        }

        int[] bulk = Enumerable.Range(0, count).ToArray();
        SwapOrNotShuffle.ShuffleList(bulk, seed);
        Assert.That(bulk, Is.EqualTo(perIndex), "backwards bulk shuffle must equal the per-index mapping");

        SwapOrNotShuffle.ShuffleList(bulk, seed, forwards: true);
        Assert.That(bulk, Is.Ordered, "forwards shuffle must invert the backwards shuffle");
    }

    [TestCase(100, 1)]
    [TestCase(9200, 2)]
    public void Committee_cache_assigns_every_active_validator_exactly_once(int validatorCount, int expectedCommitteesPerSlot)
    {
        // Every 10th validator is inactive to verify the active-set filtering.
        BeaconStateFulu state = CreateState(validatorCount, inactiveEvery: 10);
        EpochCache epochCache = new();
        CommitteeCache cache = epochCache.GetCommitteeCache(state, epoch: 0);

        int[] activeIndices = state.GetActiveValidatorIndices(0);
        List<int> assigned = [];
        int minSize = int.MaxValue, maxSize = 0;
        for (ulong slot = 0; slot < Presets.SlotsPerEpoch; slot++)
        {
            for (int index = 0; index < cache.CommitteesPerSlot; index++)
            {
                ReadOnlySpan<int> committee = cache.GetBeaconCommittee(slot, index);
                minSize = Math.Min(minSize, committee.Length);
                maxSize = Math.Max(maxSize, committee.Length);
                foreach (int validatorIndex in committee)
                {
                    assigned.Add(validatorIndex);
                }
            }
        }
        assigned.Sort();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.CommitteesPerSlot, Is.EqualTo(expectedCommitteesPerSlot));
            Assert.That(cache.ActiveValidatorCount, Is.EqualTo(activeIndices.Length));
            Assert.That(assigned, Is.EqualTo(activeIndices), "every active validator must appear exactly once per epoch");
            Assert.That(maxSize - minSize, Is.LessThanOrEqualTo(1), "committee sizes must be balanced");
            Assert.That(epochCache.GetCommitteeCache(state, 0), Is.SameAs(cache), "the LRU must reuse the cached shuffling");
        }
    }

    [TestCase(64, 32 * Gwei, 128 * Gwei, 128 * Gwei, 0 * Gwei, Description = "Floored at MIN_PER_EPOCH_CHURN_LIMIT_ELECTRA")]
    [TestCase(15_625, 2048 * Gwei, 488 * Gwei, 256 * Gwei, 232 * Gwei, Description = "TAB/quotient rounded down to increment; exit churn capped")]
    public void Churn_limits_follow_total_active_balance(int validatorCount, ulong effectiveBalance, ulong expectedBalanceChurn, ulong expectedActivationExitChurn, ulong expectedConsolidationChurn)
    {
        BeaconStateFulu state = CreateState(validatorCount, effectiveBalance);
        EpochCache cache = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.GetTotalActiveBalance(cache), Is.EqualTo((ulong)validatorCount * effectiveBalance));
            Assert.That(state.GetBalanceChurnLimit(cache), Is.EqualTo(expectedBalanceChurn));
            Assert.That(state.GetActivationExitChurnLimit(cache), Is.EqualTo(expectedActivationExitChurn));
            Assert.That(state.GetConsolidationChurnLimit(cache), Is.EqualTo(expectedConsolidationChurn));
            Assert.That(state.GetValidatorChurnLimit(cache), Is.EqualTo(Presets.MinPerEpochChurnLimit));
            Assert.That(state.GetValidatorActivationChurnLimit(cache), Is.EqualTo(Presets.MinPerEpochChurnLimit));
        }
    }

    [TestCase("0x000000000000000000000000000000000000000000000000000000000000aaaa", 32 * Gwei, 33 * Gwei, false, false, 32 * Gwei, false)]
    [TestCase("0x010000000000000000000000abcdefabcdefabcdefabcdefabcdefabcdefabcd", 32 * Gwei, 33 * Gwei, false, true, 32 * Gwei, true)]
    [TestCase("0x010000000000000000000000abcdefabcdefabcdefabcdefabcdefabcdefabcd", 31 * Gwei, 33 * Gwei, false, true, 32 * Gwei, false)]
    [TestCase("0x020000000000000000000000abcdefabcdefabcdefabcdefabcdefabcdefabcd", 2048 * Gwei, 2049 * Gwei, true, false, 2048 * Gwei, true)]
    [TestCase("0x020000000000000000000000abcdefabcdefabcdefabcdefabcdefabcdefabcd", 2048 * Gwei, 2048 * Gwei, true, false, 2048 * Gwei, false)]
    [TestCase("0x020000000000000000000000abcdefabcdefabcdefabcdefabcdefabcdefabcd", 32 * Gwei, 33 * Gwei, true, false, 2048 * Gwei, false)]
    public void Withdrawal_credential_predicates(string credentials, ulong effectiveBalance, ulong balance, bool expectCompounding, bool expectEth1, ulong expectedMaxEffectiveBalance, bool expectPartiallyWithdrawable)
    {
        Validator validator = new()
        {
            WithdrawalCredentials = new Hash256(Bytes.FromHexString(credentials)),
            EffectiveBalance = effectiveBalance,
            WithdrawableEpoch = 10,
        };
        bool expectExecution = expectCompounding || expectEth1;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validator.HasCompoundingWithdrawalCredential(), Is.EqualTo(expectCompounding));
            Assert.That(validator.HasEth1WithdrawalCredential(), Is.EqualTo(expectEth1));
            Assert.That(validator.HasExecutionWithdrawalCredential(), Is.EqualTo(expectExecution));
            Assert.That(validator.GetMaxEffectiveBalance(), Is.EqualTo(expectedMaxEffectiveBalance));
            Assert.That(validator.IsPartiallyWithdrawableValidator(balance), Is.EqualTo(expectPartiallyWithdrawable));
            Assert.That(validator.IsFullyWithdrawableValidator(balance, epoch: 10), Is.EqualTo(expectExecution), "fully withdrawable once withdrawable epoch is reached");
            Assert.That(validator.IsFullyWithdrawableValidator(balance, epoch: 9), Is.False, "not fully withdrawable before the withdrawable epoch");
        }
    }

    [Test]
    public void Exit_queue_consumes_churn_and_slashing_applies_electra_penalties()
    {
        // 64 validators * 32 ETH => balance churn floored at 128 ETH/epoch; first exit epoch is
        // compute_activation_exit_epoch(0) = 5.
        BeaconStateFulu state = CreateState(64, 32 * Gwei);
        EpochCache cache = new();
        Validator[] validators = state.Validators!;

        state.InitiateValidatorExit(1, cache);
        state.InitiateValidatorExit(1, cache); // Idempotent: a second call must not consume churn.
        state.InitiateValidatorExit(2, cache);
        state.SlashValidator(4, cache); // Consumes 32 ETH churn via the implied exit.

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validators[1].ExitEpoch, Is.EqualTo(5ul));
            Assert.That(validators[1].WithdrawableEpoch, Is.EqualTo(5 + Presets.MinValidatorWithdrawabilityDelay));
            Assert.That(state.EarliestExitEpoch, Is.EqualTo(5ul));
            Assert.That(state.ExitBalanceToConsume, Is.EqualTo(32 * Gwei), "128 - 3 * 32 ETH of churn left in epoch 5");

            Assert.That(validators[4].Slashed, Is.True);
            Assert.That(validators[4].ExitEpoch, Is.EqualTo(5ul));
            Assert.That(validators[4].WithdrawableEpoch, Is.EqualTo(Presets.EpochsPerSlashingsVector), "max(exit withdrawability, epoch + EPOCHS_PER_SLASHINGS_VECTOR)");
            Assert.That(state.Slashings![0], Is.EqualTo(32 * Gwei));
            ulong penalty = 32 * Gwei / Presets.MinSlashingPenaltyQuotientElectra;
            Assert.That(state.Balances![4], Is.EqualTo(32 * Gwei - penalty));
            // Proposer (lookahead slot 0 => validator 0) collects the full whistleblower reward.
            Assert.That(state.Balances[0], Is.EqualTo(32 * Gwei + 32 * Gwei / Presets.WhistleblowerRewardQuotientElectra));
        }

        // A fourth 32 ETH exit exceeds the remaining churn and rolls over to epoch 6.
        state.InitiateValidatorExit(5, cache);
        state.InitiateValidatorExit(6, cache);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(validators[5].ExitEpoch, Is.EqualTo(5ul));
            Assert.That(validators[6].ExitEpoch, Is.EqualTo(6ul));
            Assert.That(state.EarliestExitEpoch, Is.EqualTo(6ul));
            Assert.That(state.ExitBalanceToConsume, Is.EqualTo(96 * Gwei));
        }
    }

    [Test]
    public void Proposer_index_is_read_from_the_lookahead()
    {
        BeaconStateFulu state = CreateState(64, 32 * Gwei);
        state.Slot = 70; // Epoch 2; the lookahead covers epochs 2 and 3 (slots 64..127).
        state.ProposerLookahead = [.. Enumerable.Range(0, 64).Select(static i => (ulong)i)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.GetBeaconProposerIndex(), Is.EqualTo(6ul), "lookahead[state.slot % SLOTS_PER_EPOCH]");
            Assert.That(state.GetBeaconProposerIndex(64), Is.EqualTo(0ul));
            Assert.That(state.GetBeaconProposerIndex(95), Is.EqualTo(31ul));
            Assert.That(state.GetBeaconProposerIndex(96), Is.EqualTo(32ul), "next epoch reads the second half");
            Assert.That(state.GetBeaconProposerIndex(127), Is.EqualTo(63ul));
            Assert.That(() => state.GetBeaconProposerIndex(63), Throws.TypeOf<BeaconStateException>(), "past epoch");
            Assert.That(() => state.GetBeaconProposerIndex(128), Throws.TypeOf<BeaconStateException>(), "beyond the lookahead");
        }
    }

    [TestCase(1ul, 5ul, 2ul, 5ul, true, Description = "Double vote: same target epoch, different data")]
    [TestCase(1ul, 6ul, 2ul, 5ul, true, Description = "Surround vote: 1 surrounds 2")]
    [TestCase(2ul, 5ul, 1ul, 6ul, false, Description = "Surrounded vote is not slashable from this side")]
    [TestCase(1ul, 5ul, 1ul, 6ul, false, Description = "Different target epochs, no surround")]
    public void Attestation_data_slashability(ulong source1, ulong target1, ulong source2, ulong target2, bool expected)
    {
        AttestationData data1 = CreateAttestationData(source1, target1, beaconBlockRootByte: 1);
        AttestationData data2 = CreateAttestationData(source2, target2, beaconBlockRootByte: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BeaconStateAccessors.IsSlashableAttestationData(data1, data2), Is.EqualTo(expected));
            Assert.That(BeaconStateAccessors.IsSlashableAttestationData(data1, data1), Is.False, "identical data is never slashable");
        }
    }

    private static AttestationData CreateAttestationData(ulong sourceEpoch, ulong targetEpoch, byte beaconBlockRootByte) => new()
    {
        Slot = 100,
        BeaconBlockRoot = Hash(beaconBlockRootByte),
        Source = new Checkpoint { Epoch = sourceEpoch, Root = Hash(0x0A) },
        Target = new Checkpoint { Epoch = targetEpoch, Root = Hash(0x0B) },
    };

    /// <summary>Creates a minimal Fulu state with active validators at epoch 0 and non-zero RANDAO mixes.</summary>
    private static BeaconStateFulu CreateState(int validatorCount, ulong effectiveBalance = 32 * Gwei, int inactiveEvery = 0)
    {
        Hash256[] randaoMixes = new Hash256[(int)Presets.EpochsPerHistoricalVector];
        Array.Fill(randaoMixes, Hash(0x42));

        Validator[] validators = new Validator[validatorCount];
        ulong[] balances = new ulong[validatorCount];
        for (int i = 0; i < validatorCount; i++)
        {
            bool inactive = inactiveEvery > 0 && i % inactiveEvery == 0;
            validators[i] = new Validator
            {
                WithdrawalCredentials = Hash256.Zero,
                EffectiveBalance = effectiveBalance,
                ActivationEpoch = inactive ? Presets.FarFutureEpoch : 0,
                ExitEpoch = Presets.FarFutureEpoch,
                WithdrawableEpoch = Presets.FarFutureEpoch,
                ActivationEligibilityEpoch = 0,
            };
            balances[i] = effectiveBalance;
        }

        return new BeaconStateFulu
        {
            Slot = 0,
            Validators = validators,
            Balances = balances,
            RandaoMixes = randaoMixes,
            Slashings = new ulong[(int)Presets.EpochsPerSlashingsVector],
            ProposerLookahead = new ulong[(int)Presets.ProposerLookaheadSlots],
            FinalizedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
        };
    }

    private static Hash256 Hash(byte b)
    {
        byte[] bytes = new byte[32];
        bytes[0] = b;
        return new Hash256(bytes);
    }
}
