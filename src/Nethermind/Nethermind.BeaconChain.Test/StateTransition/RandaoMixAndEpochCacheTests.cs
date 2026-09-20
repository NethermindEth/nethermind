// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.StateTransition;

/// <summary>
/// <see cref="BeaconStateAccessors.GetRandaoMix"/> must refuse an epoch outside the historical
/// vector's window instead of silently wrapping into a different epoch's mix, and
/// <see cref="EpochCache"/>'s total-active-balance memo must refuse reuse across two states that
/// diverge before the memoized epoch's shuffling-decision slot.
/// </summary>
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
        Hash256 oldestMix = Hash(0xAA);
        Hash256 currentMix = Hash(0xBB);
        state.RandaoMixes![(int)(oldestInWindow % Presets.EpochsPerHistoricalVector)] = oldestMix;
        state.RandaoMixes[(int)(currentEpoch % Presets.EpochsPerHistoricalVector)] = currentMix;

        Assert.That(state.GetRandaoMix(oldestInWindow), Is.EqualTo(oldestMix),
            "the oldest epoch the vector still covers must resolve, and to its own mix");
        Assert.That(state.GetRandaoMix(currentEpoch), Is.EqualTo(currentMix));
    }

    [Test]
    public void GetSeed_still_resolves_previous_current_and_next_epoch_to_the_correct_mix()
    {
        // GetSeed feeds GetRandaoMix a value shifted by (EPOCHS_PER_HISTORICAL_VECTOR - lookahead -
        // 1) in the old code, now a plain "epoch - lookahead - 1"; both must land on the same slot,
        // and that slot must now pass the new bounds check instead of being rejected by it.
        ulong currentEpoch = Presets.EpochsPerHistoricalVector + 100;
        BeaconStateFulu state = CreateState(currentEpoch, validatorCount: 4);

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
            Hash256 expected = new(SHA256.HashData(preimage));

            Assert.That(seed, Is.EqualTo(expected), $"seed for epoch {epoch} must use mix epoch {mixEpoch}'s randao mix");
        }
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
    public void GetTotalActiveBalance_refuses_reuse_across_two_branches_that_diverge_before_the_decision_slot()
    {
        const ulong epoch = 5;
        BeaconStateFulu branchA = CreateBranchState(epoch, decisionSlotRoot: Hash(0xAA), validatorCount: 10);
        BeaconStateFulu branchB = CreateBranchState(epoch, decisionSlotRoot: Hash(0xBB), validatorCount: 20);

        EpochCache cache = new();
        ulong balanceA = cache.GetTotalActiveBalance(branchA);
        Assert.That(balanceA, Is.EqualTo(10UL * 32 * Gwei));

        Assert.That(() => cache.GetTotalActiveBalance(branchB), Throws.TypeOf<BeaconStateException>(),
            "a second branch's state at the same epoch must be refused, not silently given branch A's balance");
    }

    [Test]
    public void GetTotalActiveBalance_resolves_each_branch_correctly_when_given_its_own_cache()
    {
        const ulong epoch = 5;
        BeaconStateFulu branchA = CreateBranchState(epoch, decisionSlotRoot: Hash(0xAA), validatorCount: 10);
        BeaconStateFulu branchB = CreateBranchState(epoch, decisionSlotRoot: Hash(0xBB), validatorCount: 20);

        ulong balanceA = new EpochCache().GetTotalActiveBalance(branchA);
        ulong balanceB = new EpochCache().GetTotalActiveBalance(branchB);

        Assert.That(balanceA, Is.EqualTo(10UL * 32 * Gwei));
        Assert.That(balanceB, Is.EqualTo(20UL * 32 * Gwei));
    }

    [Test]
    public void GetTotalActiveBalance_reuses_the_memo_across_repeated_calls_on_the_same_branch()
    {
        const ulong epoch = 5;
        BeaconStateFulu branch = CreateBranchState(epoch, decisionSlotRoot: Hash(0xAA), validatorCount: 10);
        EpochCache cache = new();

        ulong first = cache.GetTotalActiveBalance(branch);
        ulong second = cache.GetTotalActiveBalance(branch);

        Assert.That(second, Is.EqualTo(first), "repeated calls for the same branch and epoch must not be treated as a conflict");
    }

    private static BeaconStateFulu CreateState(ulong currentEpoch, int validatorCount)
    {
        Hash256[] randaoMixes = new Hash256[(int)Presets.EpochsPerHistoricalVector];
        Array.Fill(randaoMixes, Hash(0x01));

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

        return new BeaconStateFulu
        {
            Slot = BeaconStateAccessors.ComputeStartSlotAtEpoch(currentEpoch),
            Validators = validators,
            Balances = balances,
            RandaoMixes = randaoMixes,
            BlockRoots = CreateFilledBlockRoots(),
            Slashings = new ulong[(int)Presets.EpochsPerSlashingsVector],
            ProposerLookahead = new ulong[(int)Presets.ProposerLookaheadSlots],
            FinalizedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
        };
    }

    /// <summary>A state at <paramref name="epoch"/> whose shuffling-decision-slot block root is
    /// <paramref name="decisionSlotRoot"/>, standing in for two states that diverged at or before
    /// that slot (a "conflicting fork" for <see cref="EpochCache"/> purposes).</summary>
    private static BeaconStateFulu CreateBranchState(ulong epoch, Hash256 decisionSlotRoot, int validatorCount)
    {
        BeaconStateFulu state = CreateState(epoch, validatorCount);
        Hash256 decisionRootBefore = state.GetShufflingDecisionRoot(epoch);
        Assert.That(decisionRootBefore, Is.Not.EqualTo(decisionSlotRoot), "test fixture bug: pick a distinctive root");

        ulong decisionSlot = epoch >= Presets.MinSeedLookahead
            ? BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch - Presets.MinSeedLookahead)
            : 0;
        if (decisionSlot > 0)
            decisionSlot--;
        state.BlockRoots![(int)(decisionSlot % Presets.SlotsPerHistoricalRoot)] = decisionSlotRoot;

        Assert.That(state.GetShufflingDecisionRoot(epoch), Is.EqualTo(decisionSlotRoot), "test fixture bug: root did not land on the decision slot");
        return state;
    }

    private static Hash256[] CreateFilledBlockRoots()
    {
        Hash256[] roots = new Hash256[(int)Presets.SlotsPerHistoricalRoot];
        Array.Fill(roots, Hash(0x02));
        return roots;
    }

    private static Hash256 Hash(byte b)
    {
        byte[] bytes = new byte[32];
        bytes[0] = b;
        return new Hash256(bytes);
    }
}
