// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.StateTransition;

/// <summary>
/// The Gloas seed derivation must be bounded the way the Fulu one is (the mix epoch checked raw,
/// never inflated by <c>EPOCHS_PER_HISTORICAL_VECTOR</c> before the modulo), and
/// <see cref="EpochCache"/>'s two memos must be keyed by exactly what each cached value depends on:
/// the committee shuffling by the shuffling decision root, the total active balance by the epoch
/// boundary root. Sibling states inside one epoch legitimately share both; states that diverged
/// inside the previous epoch share the first and must not share the second.
/// </summary>
public class GloasSeedAndEpochCacheTests
{
    private const ulong Gwei = 1_000_000_000;

    // ---- get_seed over BeaconStateGloas ----

    [Test]
    public void GetSeed_for_gloas_resolves_previous_current_and_next_epoch_to_the_correct_mix()
    {
        // An implementation that adds EpochsPerHistoricalVector before the modulo asks GetRandaoMix
        // for an epoch far outside its window and throws here; a correct one lands on the same slot.
        ulong currentEpoch = Presets.EpochsPerHistoricalVector + 100;
        BeaconStateGloas state = CreateGloasState(currentEpoch, validatorCount: 4);

        byte[] domain = [1, 2, 3, 4];
        Span<byte> preimage = stackalloc byte[4 + 8 + 32];
        domain.CopyTo(preimage);

        foreach (ulong epoch in new[] { currentEpoch - 1, currentEpoch, currentEpoch + 1 })
        {
            ulong mixEpoch = epoch - Presets.MinSeedLookahead - 1;
            Hash256 mix = Hash((byte)(mixEpoch % 251));
            state.RandaoMixes![(int)(mixEpoch % Presets.EpochsPerHistoricalVector)] = mix;

            Hash256 seed = state.GetSeed(epoch, domain);

            BinaryPrimitives.WriteUInt64LittleEndian(preimage[4..], epoch);
            mix.Bytes.CopyTo(preimage[12..]);
            Assert.That(seed, Is.EqualTo(new Hash256(SHA256.HashData(preimage))), $"seed for epoch {epoch} must use mix epoch {mixEpoch}'s randao mix");
        }
    }

    [Test]
    public void GetSeed_for_gloas_refuses_an_epoch_whose_mix_is_outside_the_window_instead_of_wrapping()
    {
        ulong currentEpoch = Presets.EpochsPerHistoricalVector + 100;
        BeaconStateGloas state = CreateGloasState(currentEpoch, validatorCount: 4);

        ulong mixNotYetKnown = currentEpoch + Presets.MinSeedLookahead + 2;
        Assert.That(() => state.GetSeed(mixNotYetKnown, [1, 2, 3, 4]), Throws.TypeOf<BeaconStateException>(),
            "the seed for an epoch whose mix has not been decided yet must be refused, not derived from an unrelated mix");

        ulong mixOverwritten = currentEpoch - Presets.EpochsPerHistoricalVector + Presets.MinSeedLookahead;
        Assert.That(() => state.GetSeed(mixOverwritten, [1, 2, 3, 4]), Throws.TypeOf<BeaconStateException>(),
            "the seed for an epoch whose mix the vector has already overwritten must be refused");
    }

    [Test]
    public void GetSeed_for_gloas_resolves_at_genesis_without_throwing_despite_the_lookahead_underflowing()
    {
        BeaconStateGloas state = CreateGloasState(currentEpoch: 0, validatorCount: 4);
        Assert.That(() => state.GetSeed(0, [1, 2, 3, 4]), Throws.Nothing);
    }

    [Test]
    public void GetSeed_for_gloas_equals_the_fulu_seed_over_equal_mixes()
    {
        ulong currentEpoch = 1000;
        BeaconStateGloas gloas = CreateGloasState(currentEpoch, validatorCount: 4);
        BeaconStateFulu fulu = new() { Slot = gloas.Slot, RandaoMixes = gloas.RandaoMixes };
        for (ulong e = currentEpoch - 5; e <= currentEpoch; e++)
            gloas.RandaoMixes![(int)(e % Presets.EpochsPerHistoricalVector)] = Hash((byte)e);

        foreach (ulong epoch in new[] { currentEpoch - 1, currentEpoch, currentEpoch + 1 })
        {
            Assert.That(gloas.GetSeed(epoch, DomainType.BeaconAttester), Is.EqualTo(fulu.GetSeed(epoch, DomainType.BeaconAttester)));
            Assert.That(gloas.GetSeed(epoch, DomainType.PtcAttester), Is.EqualTo(fulu.GetSeed(epoch, DomainType.PtcAttester)));
        }
        Assert.That(gloas.GetSeed(currentEpoch, DomainType.BeaconAttester), Is.Not.EqualTo(gloas.GetSeed(currentEpoch, DomainType.PtcAttester)), "the domain must reach the preimage");
    }

    // ---- EpochCache keys: what each memo depends on ----

    [Test]
    public void GetEpochBoundaryRoot_is_the_block_root_of_the_last_slot_before_the_epoch_and_zero_at_genesis()
    {
        const ulong epoch = 5;
        ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch);
        Hash256 expected = Hash(0xE5);

        Assert.That(EpochCache.GetEpochBoundaryRoot(startSlot + 7, slot => slot == startSlot - 1 ? expected : Hash(0x00)), Is.EqualTo(expected));
        Assert.That(EpochCache.GetEpochBoundaryRoot(startSlot, slot => slot == startSlot - 1 ? expected : Hash(0x00)), Is.EqualTo(expected), "the first slot of the epoch keys on the same root as the rest of it");
        Assert.That(EpochCache.GetEpochBoundaryRoot(3, _ => throw new InvalidOperationException("no block precedes the genesis epoch")), Is.EqualTo(Hash256.Zero));
    }

    [Test]
    public void GetTotalActiveBalance_refuses_two_gloas_states_that_diverged_inside_the_previous_epoch_despite_sharing_the_decision_root()
    {
        // Diverging after the decision slot but before the boundary: the shuffling agrees, the
        // effective balances (recomputed at the boundary from balances each branch's own blocks
        // moved) do not. A decision-root key would hand branch B branch A's balance silently.
        const ulong epoch = 5;
        BeaconStateGloas branchA = CreateBranchState(epoch, divergedAtSlot: DecisionSlot(epoch) + 5, branchRoot: Hash(0xAA), validatorCount: 10);
        BeaconStateGloas branchB = CreateBranchState(epoch, divergedAtSlot: DecisionSlot(epoch) + 5, branchRoot: Hash(0xBB), validatorCount: 20);
        Assert.That(branchA.GetShufflingDecisionRoot(epoch), Is.EqualTo(branchB.GetShufflingDecisionRoot(epoch)), "fixture bug: the branches must share the decision root");

        EpochCache cache = new();
        Assert.That(cache.GetTotalActiveBalance(branchA), Is.EqualTo(10UL * 32 * Gwei));
        Assert.That(() => cache.GetTotalActiveBalance(branchB), Throws.TypeOf<BeaconStateException>().With.Message.Contains("epoch boundary root"),
            "branch B's balance is not branch A's; a memo keyed on anything coarser than the boundary root cannot tell them apart");
    }

    [Test]
    public void GetTotalActiveBalance_shares_the_memo_between_same_epoch_siblings_because_effective_balances_only_change_at_the_boundary()
    {
        // One slot into the epoch, so the sibling blocks at the epoch's first slot have their roots recorded.
        const ulong epoch = 5;
        ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch);
        BeaconStateGloas siblingA = CreateBranchState(epoch, divergedAtSlot: startSlot, branchRoot: Hash(0xAA), validatorCount: 10, slotsIntoEpoch: 1);
        BeaconStateGloas siblingB = CreateBranchState(epoch, divergedAtSlot: startSlot, branchRoot: Hash(0xBB), validatorCount: 10, slotsIntoEpoch: 1);
        Assert.That(siblingA.BlockRoots![(int)(startSlot % Presets.SlotsPerHistoricalRoot)], Is.Not.EqualTo(siblingB.BlockRoots![(int)(startSlot % Presets.SlotsPerHistoricalRoot)]), "fixture bug: siblings must differ at the first slot of the epoch");

        EpochCache cache = new();
        ulong balanceA = cache.GetTotalActiveBalance(siblingA);
        Assert.That(() => cache.GetTotalActiveBalance(siblingB), Is.EqualTo(balanceA),
            "nothing a block inside the epoch does changes the current epoch's active set or effective balances, so siblings share the value");
    }

    [Test]
    public void GetCommitteeCache_shares_the_shuffling_between_states_with_the_same_decision_root_and_rebuilds_for_a_different_one()
    {
        const ulong epoch = 5;
        BeaconStateGloas branchA = CreateBranchState(epoch, divergedAtSlot: DecisionSlot(epoch) + 5, branchRoot: Hash(0xAA), validatorCount: 8);
        BeaconStateGloas branchB = CreateBranchState(epoch, divergedAtSlot: DecisionSlot(epoch) + 5, branchRoot: Hash(0xBB), validatorCount: 8);
        BeaconStateGloas otherShuffling = CreateBranchState(epoch, divergedAtSlot: DecisionSlot(epoch) - 5, branchRoot: Hash(0xCC), validatorCount: 8);

        EpochCache cache = new();
        CommitteeCache committeesA = cache.GetCommitteeCache(branchA, epoch);

        Assert.That(cache.GetCommitteeCache(branchB, epoch), Is.SameAs(committeesA), "a shuffling is fixed by the decision root; branches diverging after it share it");
        Assert.That(cache.GetCommitteeCache(otherShuffling, epoch), Is.Not.SameAs(committeesA), "a branch that diverged before the decision slot has its own shuffling");
    }

    // ---- Fixtures ----

    private static ulong DecisionSlot(ulong epoch)
    {
        ulong decisionSlot = epoch >= Presets.MinSeedLookahead ? BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch - Presets.MinSeedLookahead) : 0;
        return decisionSlot > 0 ? decisionSlot - 1 : 0;
    }

    /// <summary>
    /// A state at the first slot of <paramref name="epoch"/> on a branch that diverged from the
    /// common history at <paramref name="divergedAtSlot"/>: every block root from that slot on is
    /// <paramref name="branchRoot"/>, since forked block roots never re-converge.
    /// </summary>
    private static BeaconStateGloas CreateBranchState(ulong epoch, ulong divergedAtSlot, Hash256 branchRoot, int validatorCount, ulong slotsIntoEpoch = 0)
    {
        BeaconStateGloas state = CreateGloasState(epoch, validatorCount);
        state.Slot += slotsIntoEpoch;
        for (ulong slot = divergedAtSlot; slot < state.Slot; slot++)
            state.BlockRoots![(int)(slot % Presets.SlotsPerHistoricalRoot)] = branchRoot;
        return state;
    }

    private static BeaconStateGloas CreateGloasState(ulong currentEpoch, int validatorCount)
    {
        Hash256[] randaoMixes = new Hash256[(int)Presets.EpochsPerHistoricalVector];
        Array.Fill(randaoMixes, Hash(0x01));
        Hash256[] blockRoots = new Hash256[(int)Presets.SlotsPerHistoricalRoot];
        Array.Fill(blockRoots, Hash(0x02));

        Validator[] validators = new Validator[validatorCount];
        ulong[] balances = new ulong[validatorCount];
        for (int i = 0; i < validatorCount; i++)
        {
            validators[i] = new Validator
            {
                WithdrawalCredentials = Hash256.Zero,
                EffectiveBalance = 32 * Gwei,
                ActivationEpoch = 0,
                ExitEpoch = Presets.FarFutureEpoch,
                WithdrawableEpoch = Presets.FarFutureEpoch,
                ActivationEligibilityEpoch = 0,
            };
            balances[i] = 32 * Gwei;
        }

        return new BeaconStateGloas
        {
            Slot = BeaconStateAccessors.ComputeStartSlotAtEpoch(currentEpoch),
            Validators = validators,
            Balances = balances,
            RandaoMixes = randaoMixes,
            BlockRoots = blockRoots,
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
