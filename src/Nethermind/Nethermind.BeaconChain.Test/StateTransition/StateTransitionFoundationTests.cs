// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.StateTransition;

public class StateTransitionFoundationTests
{
    private const ulong Gwei = 1_000_000_000;

    [Test]
    public void Bulk_shuffle_matches_per_index_shuffle_and_round_trips([Values(1, 2, 33, 100, 1000, 6271)] int count)
    {
        byte[] seed = SHA256.HashData(BitConverter.GetBytes(count));

        int[] perIndex = Enumerable.Range(0, count)
            .Select(i => SwapOrNotShuffle.ComputeShuffledIndex(i, count, seed)).ToArray();

        int[] bulk = Enumerable.Range(0, count).ToArray();
        SwapOrNotShuffle.ShuffleList(bulk, seed);
        Assert.That(bulk, Is.EqualTo(perIndex), "backwards bulk shuffle must equal the per-index mapping");

        SwapOrNotShuffle.ShuffleList(bulk, seed, forwards: true);
        Assert.That(bulk, Is.Ordered, "forwards shuffle must invert the backwards shuffle");
    }

    // Spec compute_shuffled_index: disjoint parallel swaps must preserve the permutation and its inverse.
    [Test]
    [NonParallelizable]
    public void Bulk_shuffle_of_a_registry_sized_list_is_split_across_threads_and_matches_per_index_shuffle()
    {
        Assume.That(Environment.ProcessorCount, Is.GreaterThan(1), "a single processor runs the pieces in turn");
        const int count = 400_009;
        byte[] seed = SHA256.HashData(BitConverter.GetBytes(count));
        int[] bulk = Enumerable.Range(0, count).ToArray();

        using ParallelLoopCounter loops = new();
        SwapOrNotShuffle.ShuffleList(bulk, seed);

        Assert.That(loops.Begun, Is.GreaterThan(0), "pieces of the rounds ran as parallel loops");
        for (int i = 0; i < count; i += 97)
        {
            Assert.That(bulk[i], Is.EqualTo(SwapOrNotShuffle.ComputeShuffledIndex(i, count, seed)), $"position {i}");
        }

        SwapOrNotShuffle.ShuffleList(bulk, seed, forwards: true);
        Assert.That(bulk, Is.EqualTo(Enumerable.Range(0, count)), "inverse permutation");
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(cache.CommitteesPerSlot, Is.EqualTo(expectedCommitteesPerSlot));
        Assert.That(cache.ActiveValidatorCount, Is.EqualTo(activeIndices.Length));
        Assert.That(assigned, Is.EqualTo(activeIndices), "every active validator must appear exactly once per epoch");
        Assert.That(maxSize - minSize, Is.LessThanOrEqualTo(1), "committee sizes must be balanced");
        Assert.That(epochCache.GetCommitteeCache(state, 0), Is.SameAs(cache), "the LRU must reuse the cached shuffling");
    }

    [TestCase(64, 32 * Gwei, 128 * Gwei, 128 * Gwei, 0 * Gwei, Description = "Floored at MIN_PER_EPOCH_CHURN_LIMIT_ELECTRA")]
    [TestCase(15_625, 2048 * Gwei, 488 * Gwei, 256 * Gwei, 232 * Gwei, Description = "TAB/quotient rounded down to increment; exit churn capped")]
    public void Churn_limits_follow_total_active_balance(int validatorCount, ulong effectiveBalance, ulong expectedBalanceChurn, ulong expectedActivationExitChurn, ulong expectedConsolidationChurn)
    {
        BeaconStateFulu state = CreateState(validatorCount, effectiveBalance);
        EpochCache cache = new();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(state.GetTotalActiveBalance(cache), Is.EqualTo((ulong)validatorCount * effectiveBalance));
        Assert.That(state.GetBalanceChurnLimit(cache), Is.EqualTo(expectedBalanceChurn));
        Assert.That(state.GetActivationExitChurnLimit(cache), Is.EqualTo(expectedActivationExitChurn));
        Assert.That(state.GetConsolidationChurnLimit(cache), Is.EqualTo(expectedConsolidationChurn));
        Assert.That(state.GetValidatorChurnLimit(cache), Is.EqualTo(Presets.MinPerEpochChurnLimit));
        Assert.That(state.GetValidatorActivationChurnLimit(cache), Is.EqualTo(Presets.MinPerEpochChurnLimit));
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(validator.HasCompoundingWithdrawalCredential(), Is.EqualTo(expectCompounding));
        Assert.That(validator.HasEth1WithdrawalCredential(), Is.EqualTo(expectEth1));
        Assert.That(validator.HasExecutionWithdrawalCredential(), Is.EqualTo(expectExecution));
        Assert.That(validator.GetMaxEffectiveBalance(), Is.EqualTo(expectedMaxEffectiveBalance));
        Assert.That(validator.IsPartiallyWithdrawableValidator(balance), Is.EqualTo(expectPartiallyWithdrawable));
        Assert.That(validator.IsFullyWithdrawableValidator(balance, epoch: 10), Is.EqualTo(expectExecution), "fully withdrawable once withdrawable epoch is reached");
        Assert.That(validator.IsFullyWithdrawableValidator(balance, epoch: 9), Is.False, "not fully withdrawable before the withdrawable epoch");
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
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(validators[5].ExitEpoch, Is.EqualTo(5ul));
        Assert.That(validators[6].ExitEpoch, Is.EqualTo(6ul));
        Assert.That(state.EarliestExitEpoch, Is.EqualTo(6ul));
        Assert.That(state.ExitBalanceToConsume, Is.EqualTo(96 * Gwei));
    }

    [Test]
    public void Proposer_index_is_read_from_the_lookahead()
    {
        BeaconStateFulu state = CreateState(64, 32 * Gwei);
        state.Slot = 70; // Epoch 2; the lookahead covers epochs 2 and 3 (slots 64..127).
        state.ProposerLookahead = [.. Enumerable.Range(0, 64).Select(static i => (ulong)i)];

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(state.GetBeaconProposerIndex(), Is.EqualTo(6ul), "lookahead[state.slot % SLOTS_PER_EPOCH]");
        Assert.That(state.GetBeaconProposerIndex(64), Is.EqualTo(0ul));
        Assert.That(state.GetBeaconProposerIndex(95), Is.EqualTo(31ul));
        Assert.That(state.GetBeaconProposerIndex(96), Is.EqualTo(32ul), "next epoch reads the second half");
        Assert.That(state.GetBeaconProposerIndex(127), Is.EqualTo(63ul));
        Assert.That(() => state.GetBeaconProposerIndex(63), Throws.TypeOf<BeaconStateException>(), "past epoch");
        Assert.That(() => state.GetBeaconProposerIndex(128), Throws.TypeOf<BeaconStateException>(), "beyond the lookahead");
    }

    [TestCase(1ul, 5ul, 2ul, 5ul, true, Description = "Double vote: same target epoch, different data")]
    [TestCase(1ul, 6ul, 2ul, 5ul, true, Description = "Surround vote: 1 surrounds 2")]
    [TestCase(2ul, 5ul, 1ul, 6ul, false, Description = "Surrounded vote is not slashable from this side")]
    [TestCase(1ul, 5ul, 1ul, 6ul, false, Description = "Different target epochs, no surround")]
    public void Attestation_data_slashability(ulong source1, ulong target1, ulong source2, ulong target2, bool expected)
    {
        AttestationData data1 = CreateAttestationData(source1, target1, beaconBlockRootByte: 1);
        AttestationData data2 = CreateAttestationData(source2, target2, beaconBlockRootByte: 2);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(BeaconStateAccessors.IsSlashableAttestationData(data1, data2), Is.EqualTo(expected));
        Assert.That(BeaconStateAccessors.IsSlashableAttestationData(data1, data1), Is.False, "identical data is never slashable");
    }

    private static AttestationData CreateAttestationData(ulong sourceEpoch, ulong targetEpoch, byte beaconBlockRootByte) => new()
    {
        Slot = 100,
        BeaconBlockRoot = FromFirstByte(beaconBlockRootByte),
        Source = new Checkpoint { Epoch = sourceEpoch, Root = FromFirstByte(0x0A) },
        Target = new Checkpoint { Epoch = targetEpoch, Root = FromFirstByte(0x0B) },
    };

    private static BeaconStateFulu CreateState(int validatorCount, ulong effectiveBalance = 32 * Gwei, int inactiveEvery = 0) =>
        CreateMinimalFuluState(validatorCount, FromFirstByte(0x42), effectiveBalance, inactiveEvery);

    private sealed class ParallelLoopCounter : EventListener
    {
        private int _begun;

        public int Begun => Volatile.Read(ref _begun);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Threading.Tasks.Parallel.EventSource")
                EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName == "ParallelLoopBegin")
                Interlocked.Increment(ref _begun);
        }
    }
}

[TestFixture]
public class ImportHotPathAllocationTests
{
    private const int ValidatorCount = 131_072;
    private const int AttestersPerSlot = ValidatorCount / (int)Presets.SlotsPerEpoch;

    // Spec get_attesting_indices requires the index list; reward arithmetic must not allocate per credited flag.
    private const long AttestationBudgetBytes = 6L * AttestersPerSlot * sizeof(ulong);

    // Spec process_pending_deposits has no pubkey lookup when the queue is empty.
    private const long EmptyDepositQueueBudgetBytes = 64 * 1024;

    [Test]
    public void Fulu_attestation_reward_does_not_allocate_for_every_credited_flag()
    {
        BeaconStateFulu state = CreateFuluState(ValidatorCount);
        state.Slot = 1;
        EpochCache cache = new();
        Attestation attestation = WholeSlot(state, cache);

        BeaconStateFulu warm = state.Clone();
        BlockProcessing.ProcessAttestation(warm, attestation, cache, new PubkeyCache(), verifySignature: false);
        BeaconStateFulu measured = state.Clone();

        long allocated = Allocated(() => BlockProcessing.ProcessAttestation(measured, attestation, cache, new PubkeyCache(), verifySignature: false));

        AssertAttestationAllocation(allocated, measured.CurrentEpochParticipation!);
    }

    [Test]
    public void Gloas_attestation_reward_does_not_allocate_for_every_credited_flag()
    {
        BeaconStateGloas state = GloasForkTransition.UpgradeToGloas(CreateFuluStateAtBoundary(ValidatorCount), SyntheticSpec());
        EpochCache cache = new();
        AttestationData data = VoteFor(state, state.Slot - 1, state.GetPreviousEpoch(), state.GetBlockRootAtSlot(state.Slot - 1));
        CommitteeCache committees = cache.GetCommitteeCache(state, data.Target!.Epoch);
        AttestationGloas attestation = new()
        {
            Data = data,
            CommitteeBits = new BitArray(committees.CommitteesPerSlot, true) { Length = Presets.MaxCommitteesPerSlot },
            AggregationBits = new BitArray(AttestersPerSlot, true),
        };

        BeaconStateGloas warm = state.Clone();
        GloasBlockProcessing.ProcessAttestation(warm, attestation, parentSlot: state.Slot - 1, cache, new PubkeyCache(), verifySignature: false);
        BeaconStateGloas measured = state.Clone();

        long allocated = Allocated(() => GloasBlockProcessing.ProcessAttestation(measured, attestation, parentSlot: state.Slot - 1, cache, new PubkeyCache(), verifySignature: false));

        AssertAttestationAllocation(allocated, measured.PreviousEpochParticipation!);
    }

    [Test]
    public void Fulu_empty_deposit_queue_does_not_index_the_registry()
    {
        BeaconStateFulu state = CreateFuluState(ValidatorCount);
        GiveEveryValidatorItsOwnPubkey(state.Validators!);
        EpochCache cache = new();
        _ = cache.GetTotalActiveBalance(state);

        long allocated = Allocated(() => EpochProcessing.ProcessPendingDeposits(state, cache));

        Assert.That(allocated, Is.LessThan(EmptyDepositQueueBudgetBytes), "bytes allocated with no deposit pending");
    }

    [Test]
    public void Gloas_empty_deposit_queue_does_not_index_the_registry()
    {
        BeaconStateGloas state = GloasForkTransition.UpgradeToGloas(CreateFuluStateAtBoundary(ValidatorCount), SyntheticSpec());
        GiveEveryValidatorItsOwnPubkey(state.Validators!);
        EpochCache cache = new();
        _ = cache.GetTotalActiveBalance(state);

        long allocated = Allocated(() => GloasEpochProcessing.ProcessPendingDeposits(state, cache));

        Assert.That(allocated, Is.LessThan(EmptyDepositQueueBudgetBytes), "bytes allocated with no deposit pending");
    }

    private static Attestation WholeSlot(BeaconStateFulu state, EpochCache cache)
    {
        CommitteeCache committees = cache.GetCommitteeCache(state, 0);
        BitArray committeeBits = new(committees.CommitteesPerSlot, true) { Length = Presets.MaxCommitteesPerSlot };

        return new Attestation
        {
            AggregationBits = new BitArray(AttestersPerSlot, true),
            Data = new AttestationData
            {
                Slot = 0,
                Index = 0,
                BeaconBlockRoot = state.GetBlockRootAtSlot(0),
                Source = state.CurrentJustifiedCheckpoint,
                Target = new Checkpoint { Epoch = 0, Root = state.GetBlockRoot(0) },
            },
            CommitteeBits = committeeBits,
        };
    }

    private static void GiveEveryValidatorItsOwnPubkey(Validator[] validators)
    {
        byte[] bytes = new byte[BlsPublicKey.Length];
        for (int i = 0; i < validators.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes, i);
            validators[i].Pubkey = new BlsPublicKey(bytes);
        }
    }

    private static void AssertAttestationAllocation(long allocated, byte[] participation)
    {
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(allocated, Is.LessThan(AttestationBudgetBytes), "bytes allocated by one slot-wide attestation");
        Assert.That(participation.Count(static flags => flags != 0), Is.EqualTo(AttestersPerSlot), "every attester must be credited");
    }

    private static long Allocated(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
