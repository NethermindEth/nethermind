// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.StateTransition;

public class RandaoMixAndEpochCacheTests
{
    private const ulong Gwei = 1_000_000_000;

    [Test]
    public void GetRandaoMix_refuses_an_epoch_outside_the_historical_vector_window()
    {
        // currentEpoch chosen well past EpochsPerHistoricalVector so "currentEpoch - epoch" for an
        // out-of-window request is a normal positive number, not a wrapped one.
        ulong currentEpoch = Presets.EpochsPerHistoricalVector + 100;
        BeaconStateFulu state = CreateState(currentEpoch, validatorCount: 4);

        ulong oneTooOld = currentEpoch - Presets.EpochsPerHistoricalVector - 1;
        Assert.That(() => state.GetRandaoMix(oneTooOld), Throws.TypeOf<BeaconStateException>()
            .With.Message.Contains(oneTooOld.ToString())
            .And.Message.Contains(Presets.EpochsPerHistoricalVector.ToString()));

        ulong future = currentEpoch + 1;
        Assert.That(() => state.GetRandaoMix(future), Throws.TypeOf<BeaconStateException>(),
            "a mix for an epoch that has not happened yet must also be refused, not wrapped");
    }

    [Test]
    public void GetRandaoMix_still_resolves_the_oldest_and_newest_epochs_still_inside_the_window()
    {
        ulong currentEpoch = Presets.EpochsPerHistoricalVector + 100;
        BeaconStateFulu state = CreateState(currentEpoch, validatorCount: 4);

        // Stamp the two boundary slots of the window with distinctive, non-default mixes so the
        // test would fail if the accessor read the wrong slot as well as if it threw at all.
        ulong oldestInWindow = currentEpoch - Presets.EpochsPerHistoricalVector + 1;
        Hash256 oldestMix = FromFirstByte(0xAA);
        Hash256 currentMix = FromFirstByte(0xBB);
        state.RandaoMixes![(int)(oldestInWindow % Presets.EpochsPerHistoricalVector)] = oldestMix;
        state.RandaoMixes[(int)(currentEpoch % Presets.EpochsPerHistoricalVector)] = currentMix;

        Assert.That(state.GetRandaoMix(oldestInWindow), Is.EqualTo(oldestMix),
            "the oldest epoch the vector still covers must resolve, and to its own mix");
        Assert.That(state.GetRandaoMix(currentEpoch), Is.EqualTo(currentMix));
    }

    [Test]
    public void GetSeed_still_resolves_previous_current_and_next_epoch_to_the_correct_mix()
    {
        // Seed lookback and the historical-window check must resolve the same mix.
        ulong currentEpoch = Presets.EpochsPerHistoricalVector + 100;
        BeaconStateFulu state = CreateState(currentEpoch, validatorCount: 4);

        AssertSeedMatchesReference(currentEpoch, state.RandaoMixes!, (epoch, domain) => state.GetSeed(epoch, domain));
    }

    [Test]
    public void GetSeed_resolves_at_genesis_without_throwing_despite_the_lookahead_underflowing()
    {
        // epoch=0 makes "epoch - MinSeedLookahead - 1" underflow to a huge ulong; the window check
        // must still accept it because the whole vector is genesis-seeded, matching spec behaviour.
        BeaconStateFulu state = CreateState(currentEpoch: 0, validatorCount: 4);
        Assert.That(() => state.GetSeed(0, [1, 2, 3, 4]), Throws.Nothing);
    }

    [Test]
    public void GetTotalActiveBalance_reuses_the_same_branch_memo_and_refuses_cross_branch_reuse()
    {
        const ulong epoch = 5;
        BeaconStateFulu branchA = CreateBranchState(epoch, decisionSlotRoot: FromFirstByte(0xAA), validatorCount: 10);
        BeaconStateFulu branchB = CreateBranchState(epoch, decisionSlotRoot: FromFirstByte(0xBB), validatorCount: 20);

        EpochCache cache = new();
        ulong balanceA = cache.GetTotalActiveBalance(branchA);
        Assert.That(balanceA, Is.EqualTo(10UL * 32 * Gwei));
        Assert.That(new EpochCache().GetTotalActiveBalance(branchB), Is.EqualTo(20UL * 32 * Gwei));
        Assert.That(cache.GetTotalActiveBalance(branchA), Is.EqualTo(balanceA), "repeated calls for the same branch and epoch must not be treated as a conflict");

        Assert.That(() => cache.GetTotalActiveBalance(branchB), Throws.TypeOf<BeaconStateException>(),
            "a second branch's state at the same epoch must be refused, not silently given branch A's balance");
    }

    [Test]
    public void GetTotalActiveBalance_refuses_reuse_across_two_branches_that_diverged_inside_the_previous_epoch()
    {
        // Same shuffling decision root (the fork happened after that slot), different boundary root:
        // exactly the pair a decision-root key hands the wrong balance to without a word.
        const ulong epoch = 5;
        ulong forkSlot = DecisionSlot(epoch) + 5;
        BeaconStateFulu branchA = CreateBranchState(epoch, forkSlot, branchRoot: FromFirstByte(0xAA), validatorCount: 10);
        BeaconStateFulu branchB = CreateBranchState(epoch, forkSlot, branchRoot: FromFirstByte(0xBB), validatorCount: 20);
        Assert.That(branchA.GetShufflingDecisionRoot(epoch), Is.EqualTo(branchB.GetShufflingDecisionRoot(epoch)), "test fixture bug: the branches must share the decision root");

        EpochCache cache = new();
        Assert.That(cache.GetTotalActiveBalance(branchA), Is.EqualTo(10UL * 32 * Gwei));
        Assert.That(() => cache.GetTotalActiveBalance(branchB), Throws.TypeOf<BeaconStateException>().With.Message.Contains("epoch boundary root"),
            "branch B recomputed its effective balances from its own blocks at the boundary; it must not be handed branch A's total");
    }

    [Test]
    public void GetTotalActiveBalance_shares_the_memo_between_siblings_that_diverged_inside_the_memoized_epoch()
    {
        // Blocks inside the epoch move raw balances only; the active set and effective balances the
        // total is built from were fixed at the boundary both siblings share.
        // One slot into the epoch, so the sibling blocks at the epoch's first slot have their roots recorded.
        const ulong epoch = 5;
        ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch);
        BeaconStateFulu siblingA = CreateBranchState(epoch, startSlot, branchRoot: FromFirstByte(0xAA), validatorCount: 10, slotsIntoEpoch: 1);
        BeaconStateFulu siblingB = CreateBranchState(epoch, startSlot, branchRoot: FromFirstByte(0xBB), validatorCount: 10, slotsIntoEpoch: 1);
        Assert.That(siblingA.GetBlockRootAtSlot(startSlot), Is.Not.EqualTo(siblingB.GetBlockRootAtSlot(startSlot)), "test fixture bug: siblings must differ at the first slot of the epoch");

        EpochCache cache = new();
        ulong balanceA = cache.GetTotalActiveBalance(siblingA);
        Assert.That(() => cache.GetTotalActiveBalance(siblingB), Is.EqualTo(balanceA), "same-epoch siblings legitimately share the total");
    }

    internal static void AssertSeedMatchesReference(ulong currentEpoch, Hash256[] randaoMixes, Func<ulong, byte[], Hash256> getSeed)
    {
        byte[] domain = [1, 2, 3, 4];
        Span<byte> preimage = stackalloc byte[4 + 8 + 32];
        domain.CopyTo(preimage);

        foreach (ulong epoch in new[] { currentEpoch - 1, currentEpoch, currentEpoch + 1 })
        {
            ulong mixEpoch = epoch - Presets.MinSeedLookahead - 1;
            Hash256 mix = FromFirstByte((byte)(mixEpoch % 251));
            randaoMixes[(int)(mixEpoch % Presets.EpochsPerHistoricalVector)] = mix;

            Hash256 seed = getSeed(epoch, domain);

            BinaryPrimitives.WriteUInt64LittleEndian(preimage[4..], epoch);
            mix.Bytes.CopyTo(preimage[12..]);
            Hash256 expected = new(SHA256.HashData(preimage));

            Assert.That(seed, Is.EqualTo(expected), $"seed for epoch {epoch} must use mix epoch {mixEpoch}'s randao mix");
        }
    }

    internal static BeaconStateFulu CreateState(ulong currentEpoch, int validatorCount)
    {
        BeaconStateFulu state = GloasTestFixtures.CreateMinimalFuluState(validatorCount, FromFirstByte(0x01));
        state.Slot = BeaconStateAccessors.ComputeStartSlotAtEpoch(currentEpoch);
        state.BlockRoots = CreateFilledBlockRoots();
        return state;
    }

    private static BeaconStateFulu CreateBranchState(ulong epoch, Hash256 decisionSlotRoot, int validatorCount)
    {
        BeaconStateFulu state = CreateBranchState(epoch, DecisionSlot(epoch), decisionSlotRoot, validatorCount);
        Assert.That(state.GetShufflingDecisionRoot(epoch), Is.EqualTo(decisionSlotRoot), "test fixture bug: root did not land on the decision slot");
        return state;
    }

    private static BeaconStateFulu CreateBranchState(ulong epoch, ulong forkSlot, Hash256 branchRoot, int validatorCount, ulong slotsIntoEpoch = 0)
    {
        BeaconStateFulu state = CreateState(epoch, validatorCount);
        state.Slot += slotsIntoEpoch;
        Assert.That(state.BlockRoots![0], Is.Not.EqualTo(branchRoot), "test fixture bug: pick a distinctive root");
        for (ulong slot = forkSlot; slot < state.Slot; slot++)
            state.BlockRoots[(int)(slot % Presets.SlotsPerHistoricalRoot)] = branchRoot;
        return state;
    }

    private static ulong DecisionSlot(ulong epoch)
    {
        ulong decisionSlot = epoch >= Presets.MinSeedLookahead ? BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch - Presets.MinSeedLookahead) : 0;
        return decisionSlot > 0 ? decisionSlot - 1 : 0;
    }

    private static Hash256[] CreateFilledBlockRoots() => Enumerable.Repeat(FromFirstByte(0x02), (int)Presets.SlotsPerHistoricalRoot).ToArray();

    [Test]
    public void Sync_committee_indices_map_repeated_members_and_follow_a_new_committee()
    {
        Validator[] validators = [.. new[] { 1, 2, 3, 4 }.Select(static b => new Validator { Pubkey = Pubkey((byte)b) })];
        SyncCommittee first = new() { Pubkeys = [Pubkey(3), Pubkey(1), Pubkey(3)] };
        SyncCommittee second = new() { Pubkeys = [Pubkey(4), Pubkey(2)] };
        EpochCache cache = new();

        int[] firstIndices = cache.GetSyncCommitteeIndices(first, validators);
        int[] secondIndices = cache.GetSyncCommitteeIndices(second, validators);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(firstIndices, Is.EqualTo(new[] { 2, 0, 2 }));
        Assert.That(secondIndices, Is.EqualTo(new[] { 3, 1 }));
        Assert.That(cache.GetSyncCommitteeIndices(second, validators), Is.SameAs(secondIndices), "the same committee reuses its indices");
        Assert.Throws<BeaconStateException>(() => cache.GetSyncCommitteeIndices(new SyncCommittee { Pubkeys = [Pubkey(9)] }, validators));
    }

    [Test]
    public void Sync_committee_indices_mark_a_missing_member_and_look_it_up_again()
    {
        Validator[] validators = [.. new[] { 1, 2 }.Select(static b => new Validator { Pubkey = Pubkey((byte)b) })];
        SyncCommittee committee = new() { Pubkeys = [Pubkey(2), Pubkey(9)] };
        EpochCache cache = new();

        int[] missing = cache.FindSyncCommitteeIndices(committee, validators);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(missing, Is.EqualTo(new[] { 1, -1 }));
        Assert.Throws<BeaconStateException>(() => cache.GetSyncCommitteeIndices(committee, validators));
        Assert.That(cache.GetSyncCommitteeIndices(committee, [.. validators, new Validator { Pubkey = Pubkey(9) }]), Is.EqualTo(new[] { 1, 2 }));
    }

    private static BlsPublicKey Pubkey(byte b)
    {
        byte[] bytes = new byte[48];
        bytes[0] = b;
        return new BlsPublicKey(bytes);
    }
}
