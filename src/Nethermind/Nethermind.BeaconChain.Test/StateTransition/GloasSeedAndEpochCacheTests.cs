// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.StateTransition;

[HardTimeout(60_000)]
public class GloasSeedAndEpochCacheTests
{
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
    public void GetSeed_for_gloas_equals_the_fulu_seed_over_equal_mixes()
    {
        ulong currentEpoch = 1000;
        BeaconStateGloas gloas = CreateGloasState(currentEpoch, validatorCount: 4);
        BeaconStateFulu fulu = new() { Slot = gloas.Slot, RandaoMixes = gloas.RandaoMixes };
        for (ulong e = currentEpoch - 5; e <= currentEpoch; e++)
            gloas.RandaoMixes![(int)(e % Presets.EpochsPerHistoricalVector)] = FromFirstByte((byte)e);

        foreach (ulong epoch in new[] { currentEpoch - 1, currentEpoch, currentEpoch + 1 })
        {
            Assert.That(gloas.GetSeed(epoch, DomainType.BeaconAttester), Is.EqualTo(fulu.GetSeed(epoch, DomainType.BeaconAttester)));
            Assert.That(gloas.GetSeed(epoch, DomainType.PtcAttester), Is.EqualTo(fulu.GetSeed(epoch, DomainType.PtcAttester)));
        }
        Assert.That(gloas.GetSeed(currentEpoch, DomainType.BeaconAttester), Is.Not.EqualTo(gloas.GetSeed(currentEpoch, DomainType.PtcAttester)), "the domain must reach the preimage");
    }

    [Test]
    public void GetEpochBoundaryRoot_is_the_block_root_of_the_last_slot_before_the_epoch_and_zero_at_genesis()
    {
        const ulong epoch = 5;
        ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch);
        Hash256 expected = FromFirstByte(0xE5);

        Assert.That(EpochCache.GetEpochBoundaryRoot(startSlot + 7, slot => slot == startSlot - 1 ? expected : FromFirstByte(0x00)), Is.EqualTo(expected));
        Assert.That(EpochCache.GetEpochBoundaryRoot(startSlot, slot => slot == startSlot - 1 ? expected : FromFirstByte(0x00)), Is.EqualTo(expected), "the first slot of the epoch keys on the same root as the rest of it");
        Assert.That(EpochCache.GetEpochBoundaryRoot(3, _ => throw new InvalidOperationException("no block precedes the genesis epoch")), Is.EqualTo(Hash256.Zero));
    }

    [Test]
    public void GetCommitteeCache_shares_the_shuffling_between_states_with_the_same_decision_root_and_rebuilds_for_a_different_one()
    {
        const ulong epoch = 5;
        BeaconStateGloas branchA = CreateBranchState(epoch, divergedAtSlot: DecisionSlot(epoch) + 5, branchRoot: FromFirstByte(0xAA), validatorCount: 8);
        BeaconStateGloas branchB = CreateBranchState(epoch, divergedAtSlot: DecisionSlot(epoch) + 5, branchRoot: FromFirstByte(0xBB), validatorCount: 8);
        BeaconStateGloas otherShuffling = CreateBranchState(epoch, divergedAtSlot: DecisionSlot(epoch) - 5, branchRoot: FromFirstByte(0xCC), validatorCount: 8);

        EpochCache cache = new();
        CommitteeCache committeesA = cache.GetCommitteeCache(branchA, epoch);

        Assert.That(cache.GetCommitteeCache(branchB, epoch), Is.SameAs(committeesA), "a shuffling is fixed by the decision root; branches diverging after it share it");
        Assert.That(cache.GetCommitteeCache(otherShuffling, epoch), Is.Not.SameAs(committeesA), "a branch that diverged before the decision slot has its own shuffling");
    }

    [Test]
    public void GetCommitteeCache_keeps_adjacent_epochs_apart_when_they_share_a_decision_root([Values(0UL, 5UL)] ulong currentEpoch)
    {
        // Epochs 0 and 1 both resolve to the genesis root; later, an epoch with only skipped slots
        // leaves the next epoch on the same root. get_seed still mixes in the epoch, so the shufflings differ.
        BeaconStateGloas state = CreateGloasState(currentEpoch, validatorCount: 8);
        ulong nextEpoch = currentEpoch + 1;
        Assert.That(state.GetShufflingDecisionRoot(nextEpoch), Is.EqualTo(state.GetShufflingDecisionRoot(currentEpoch)), "fixture bug: the epochs must share the decision root");

        EpochCache cache = new();
        CommitteeCache current = cache.GetCommitteeCache(state, currentEpoch);
        CommitteeCache next = cache.GetCommitteeCache(state, nextEpoch);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(next, Is.Not.SameAs(current), "a decision-root match alone must not serve another epoch's committees");
        Assert.That(next.Epoch, Is.EqualTo(nextEpoch));
    }

    [Test]
    public void Committees_of_a_fulu_state_and_the_gloas_state_upgraded_from_it_are_identical_so_the_shared_cache_may_serve_either([Values(-1, 0, 1)] int epochOffset)
    {
        // Sound only while specs/gloas/beacon-chain.md leaves every committee accessor unmodified and
        // upgrade_to_gloas (specs/gloas/fork.md) copies validators, randao_mixes and block_roots.
        BeaconStateFulu fulu = CreateUpgradableFuluState();
        BeaconStateGloas gloas = GloasForkTransition.UpgradeToGloas(fulu, GloasTestFixtures.SyntheticSpec(UpgradeEpoch));
        ulong epoch = (ulong)((long)UpgradeEpoch + epochOffset);
        int[][] expected = ExpectedCommittees(epoch);

        Hash256 expectedDecisionRoot = SlotRoot(DecisionSlot(epoch));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fulu.GetShufflingDecisionRoot(epoch), Is.EqualTo(expectedDecisionRoot), "fulu decision root");
            Assert.That(gloas.GetShufflingDecisionRoot(epoch), Is.EqualTo(expectedDecisionRoot), "gloas decision root");
            AssertCommittees(CommitteeCache.Build(fulu, epoch), epoch, expected, "fulu");
            AssertCommittees(CommitteeCache.Build(gloas, epoch), epoch, expected, "gloas");
        }

        EpochCache cache = new();
        CommitteeCache builtFromFulu = cache.GetCommitteeCache(fulu, epoch);
        Assert.That(cache.GetCommitteeCache(gloas, epoch), Is.SameAs(builtFromFulu),
            "a lineage that crosses the fork keeps its EpochCache, so the post-fork lookup must hit the pre-fork entry");
    }

    private const ulong UpgradeEpoch = 10;

    // Active-set bands around UpgradeEpoch: the band boundaries make each epoch's active set
    // different, so an accessor that reads the wrong epoch cannot produce the expected committees.
    private const int AlwaysActive = 8192;
    private const int ExitAtUpgrade = 64;
    private const int ActivateAfterUpgrade = 64;
    private const int ExitAfterUpgrade = 64;
    private const int ExitTwoAfterUpgrade = 64;

    private static BeaconStateFulu CreateUpgradableFuluState()
    {
        BeaconStateFulu state = GloasTestFixtures.CreateFuluState(AlwaysActive + ExitAtUpgrade + ActivateAfterUpgrade + ExitAfterUpgrade + ExitTwoAfterUpgrade);
        state.Slot = BeaconStateAccessors.ComputeStartSlotAtEpoch(UpgradeEpoch);
        for (ulong slot = 0; slot < state.Slot; slot++)
            state.BlockRoots![(int)(slot % Presets.SlotsPerHistoricalRoot)] = SlotRoot(slot);
        for (ulong mixEpoch = 0; mixEpoch <= UpgradeEpoch; mixEpoch++)
            state.RandaoMixes![(int)(mixEpoch % Presets.EpochsPerHistoricalVector)] = EpochMix(mixEpoch);

        Validator[] validators = state.Validators!;
        int band = AlwaysActive;
        for (int i = 0; i < ExitAtUpgrade; i++)
            validators[band + i].ExitEpoch = UpgradeEpoch;
        band += ExitAtUpgrade;
        for (int i = 0; i < ActivateAfterUpgrade; i++)
            validators[band + i].ActivationEpoch = UpgradeEpoch + 1;
        band += ActivateAfterUpgrade;
        for (int i = 0; i < ExitAfterUpgrade; i++)
            validators[band + i].ExitEpoch = UpgradeEpoch + 1;
        band += ExitAfterUpgrade;
        for (int i = 0; i < ExitTwoAfterUpgrade; i++)
            validators[band + i].ExitEpoch = UpgradeEpoch + 2;
        return state;
    }

    // Independent committee oracle: per-index compute_shuffled_index/compute_committee, never CommitteeCache.
    private static int[][] ExpectedCommittees(ulong epoch)
    {
        List<int> active = [];
        int band = 0;
        active.AddRange(Enumerable.Range(band, AlwaysActive));
        band += AlwaysActive;
        if (epoch < UpgradeEpoch)
            active.AddRange(Enumerable.Range(band, ExitAtUpgrade));
        band += ExitAtUpgrade;
        if (epoch > UpgradeEpoch)
            active.AddRange(Enumerable.Range(band, ActivateAfterUpgrade));
        band += ActivateAfterUpgrade;
        if (epoch <= UpgradeEpoch)
            active.AddRange(Enumerable.Range(band, ExitAfterUpgrade));
        band += ExitAfterUpgrade;
        if (epoch <= UpgradeEpoch + 1)
            active.AddRange(Enumerable.Range(band, ExitTwoAfterUpgrade));

        // get_seed: sha256(DOMAIN_BEACON_ATTESTER + uint_to_bytes(epoch) + mix of epoch - MIN_SEED_LOOKAHEAD - 1).
        byte[] preimage = new byte[4 + 8 + 32];
        preimage[0] = 0x01;
        BinaryPrimitives.WriteUInt64LittleEndian(preimage.AsSpan(4), epoch);
        EpochMix(epoch - Presets.MinSeedLookahead - 1).Bytes.CopyTo(preimage.AsSpan(12));
        byte[] seed = SHA256.HashData(preimage);

        int committeesPerSlot = Math.Clamp(active.Count / (int)Presets.SlotsPerEpoch / Presets.TargetCommitteeSize, 1, Presets.MaxCommitteesPerSlot);
        int count = committeesPerSlot * (int)Presets.SlotsPerEpoch;
        int[][] committees = new int[count][];
        for (int index = 0; index < count; index++)
        {
            int start = (int)((long)active.Count * index / count);
            int end = (int)((long)active.Count * (index + 1) / count);
            committees[index] = [.. Enumerable.Range(start, end - start).Select(i => active[SwapOrNotShuffle.ComputeShuffledIndex(i, active.Count, seed)])];
        }
        return committees;
    }

    private static void AssertCommittees(CommitteeCache actual, ulong epoch, int[][] expected, string fork)
    {
        int committeesPerSlot = expected.Length / (int)Presets.SlotsPerEpoch;
        Assert.That(actual.CommitteesPerSlot, Is.EqualTo(committeesPerSlot), $"{fork} committees per slot");
        if (actual.CommitteesPerSlot != committeesPerSlot)
            return;

        ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch);
        for (int slotOffset = 0; slotOffset < (int)Presets.SlotsPerEpoch; slotOffset++)
        {
            for (int index = 0; index < committeesPerSlot; index++)
            {
                Assert.That(actual.GetBeaconCommittee(startSlot + (ulong)slotOffset, index).ToArray(), Is.EqualTo(expected[slotOffset * committeesPerSlot + index]),
                    $"{fork} committee {index} at slot {startSlot + (ulong)slotOffset}");
            }
        }
    }

    private static Hash256 SlotRoot(ulong slot) => Tagged(0xB0, slot);
    private static Hash256 EpochMix(ulong epoch) => Tagged(0xA0, epoch);

    private static Hash256 Tagged(byte tag, ulong value)
    {
        byte[] bytes = new byte[32];
        bytes[0] = tag;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1), value);
        return new Hash256(bytes);
    }

    private static ulong DecisionSlot(ulong epoch)
    {
        ulong decisionSlot = epoch >= Presets.MinSeedLookahead ? BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch - Presets.MinSeedLookahead) : 0;
        return decisionSlot > 0 ? decisionSlot - 1 : 0;
    }

    internal static BeaconStateGloas CreateBranchState(ulong epoch, ulong divergedAtSlot, Hash256 branchRoot, int validatorCount, ulong slotsIntoEpoch = 0)
    {
        BeaconStateGloas state = CreateGloasState(epoch, validatorCount);
        state.Slot += slotsIntoEpoch;
        for (ulong slot = divergedAtSlot; slot < state.Slot; slot++)
            state.BlockRoots![(int)(slot % Presets.SlotsPerHistoricalRoot)] = branchRoot;
        return state;
    }

    internal static BeaconStateGloas CreateGloasState(ulong currentEpoch, int validatorCount)
    {
        BeaconStateFulu state = RandaoMixAndEpochCacheTests.CreateState(currentEpoch, validatorCount);
        return new BeaconStateGloas
        {
            Slot = state.Slot,
            Validators = state.Validators,
            Balances = state.Balances,
            RandaoMixes = state.RandaoMixes,
            BlockRoots = state.BlockRoots,
            Slashings = state.Slashings,
            ProposerLookahead = state.ProposerLookahead,
            FinalizedCheckpoint = state.FinalizedCheckpoint,
        };
    }
}
