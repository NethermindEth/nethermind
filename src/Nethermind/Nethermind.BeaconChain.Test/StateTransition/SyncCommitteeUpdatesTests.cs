// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.StateTransition;

/// <summary>
/// Altair <c>process_sync_committee_updates</c> in the Fulu and Gloas epoch pipelines: at
/// <c>next_epoch % EPOCHS_PER_SYNC_COMMITTEE_PERIOD == 0</c> the old next committee becomes current and a
/// fresh <c>get_next_sync_committee</c> becomes next; at every other epoch both are left as they are.
/// </summary>
public class SyncCommitteeUpdatesTests
{
    private const int ValidatorCount = 64;
    private const ulong Period = Presets.EpochsPerSyncCommitteePeriod;

    // Active at the last epoch of the period but not the next one, so a committee sampled at the current epoch can pick them.
    private static readonly int[] ExitingAtNextEpoch = [3, 17, 40];
    // Active only from the next epoch, the one get_next_sync_committee samples.
    private static readonly int[] ActivatingAtNextEpoch = [5, 33];

    private static readonly ForkUnderTest[] Forks =
    [
        new("Fulu", FuluState, static s => EpochProcessing.ProcessSyncCommitteeUpdates((BeaconStateFulu)s)),
        new("Gloas", GloasState, static s => GloasEpochProcessing.ProcessSyncCommitteeUpdates((BeaconStateGloas)s)),
    ];

    [Test]
    public void At_the_last_epoch_of_a_period_next_becomes_current_and_a_fresh_committee_of_next_epoch_validators_becomes_next(
        [ValueSource(nameof(Forks))] ForkUnderTest fork,
        [Values(Period - 1, 2 * Period - 1)] ulong epoch,
        [Values] bool unequalBalances)
    {
        ulong nextEpoch = epoch + 1;
        IBeaconStateView state = fork.Create(epoch, unequalBalances);
        SyncCommittee oldNext = state.NextSyncCommittee;
        int[] expectedIndices = ReferenceNextSyncCommitteeIndices(state, nextEpoch);

        fork.Process(state.State);

        SyncCommittee newNext = state.NextSyncCommittee;
        Dictionary<BlsPublicKey, int> indexByPubkey = [];
        for (int i = 0; i < state.Validators.Length; i++)
            indexByPubkey[state.Validators[i].Pubkey] = i;

        BlsSigner.AggregatedPublicKey expectedAggregate = new();
        int[] memberIndices = new int[newNext.Pubkeys!.Length];
        bool everyMemberKnown = true;
        for (int i = 0; i < memberIndices.Length; i++)
        {
            everyMemberKnown &= indexByPubkey.TryGetValue(newNext.Pubkeys[i], out memberIndices[i]);
            Assert.That(expectedAggregate.TryAggregate(new Bls.P1(ValidatorKey(memberIndices[i])).Compress(), out _), Is.True);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.CurrentSyncCommittee, Is.SameAs(oldNext), "current is the old next committee");
            Assert.That(newNext, Is.Not.SameAs(oldNext), "next is recomputed");
            Assert.That(newNext.Pubkeys, Has.Length.EqualTo(Presets.SyncCommitteeSize));
            Assert.That(everyMemberKnown, Is.True, "every member is a registry validator");
            Assert.That(Array.TrueForAll(memberIndices, i => state.Validators[i].IsActiveValidator(nextEpoch)), Is.True, "every member is active at the next epoch");
            Assert.That(Array.Exists(memberIndices, i => Array.IndexOf(ActivatingAtNextEpoch, i) >= 0), Is.True, "validators activating at the next epoch are eligible");
            Assert.That(Array.TrueForAll(memberIndices, i => state.Validators[i].EffectiveBalance > 0), Is.True, "a zero effective balance is never accepted");
            Assert.That(new HashSet<int>(memberIndices), Has.Count.GreaterThan(ValidatorCount / 2), "members are sampled across the registry");
            Assert.That(memberIndices, Is.EqualTo(expectedIndices), "members are get_next_sync_committee_indices in order");
            Assert.That(newNext.AggregatePubkey, Is.EqualTo(new BlsPublicKey(expectedAggregate.PublicKey.Compress())), "aggregate is the BLS sum of the member pubkeys");
        }
    }

    [Test]
    public void At_any_epoch_other_than_the_last_of_a_period_both_committees_are_unchanged(
        [ValueSource(nameof(Forks))] ForkUnderTest fork,
        [Values(Period - 2, Period, 2 * Period - 2)] ulong epoch)
    {
        IBeaconStateView state = fork.Create(epoch, false);
        SyncCommittee oldCurrent = state.CurrentSyncCommittee;
        SyncCommittee oldNext = state.NextSyncCommittee;

        fork.Process(state.State);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.CurrentSyncCommittee, Is.SameAs(oldCurrent));
            Assert.That(state.NextSyncCommittee, Is.SameAs(oldNext));
        }
    }

    /// <summary>Electra <c>get_next_sync_committee_indices</c>, which Gloas keeps as <c>compute_balance_weighted_selection</c> with <c>shuffle_indices=True</c>.</summary>
    private static int[] ReferenceNextSyncCommitteeIndices(IBeaconStateView state, ulong nextEpoch)
    {
        int[] active = state.GetActiveValidatorIndices(nextEpoch);
        byte[] seed = state.GetSeed(nextEpoch).BytesToArray();
        byte[] preimage = new byte[40];
        seed.CopyTo(preimage, 0);
        List<int> indices = [];
        for (ulong i = 0; indices.Count < Presets.SyncCommitteeSize; i++)
        {
            int candidate = active[SwapOrNotShuffle.ComputeShuffledIndex((int)(i % (ulong)active.Length), active.Length, seed)];
            BinaryPrimitives.WriteUInt64LittleEndian(preimage.AsSpan(32), i / 16);
            ulong randomValue = BinaryPrimitives.ReadUInt16LittleEndian(SHA256.HashData(preimage).AsSpan((int)(i % 16 * 2)));
            if (state.Validators[candidate].EffectiveBalance * ushort.MaxValue >= Presets.MaxEffectiveBalanceElectra * randomValue)
                indices.Add(candidate);
        }
        return [.. indices];
    }

    [Test]
    public void A_committee_member_with_an_invalid_pubkey_fails_the_rotation([ValueSource(nameof(Forks))] ForkUnderTest fork)
    {
        IBeaconStateView state = fork.Create(Period - 1, false);
        foreach (Validator validator in state.Validators)
            validator.Pubkey = new BlsPublicKey(new byte[BlsPublicKey.Length]);

        Assert.Throws<BeaconStateException>(() => fork.Process(state.State));
    }

    public sealed record ForkUnderTest(string Name, Func<ulong, bool, IBeaconStateView> Create, Action<object> Process)
    {
        public override string ToString() => Name;
    }

    public interface IBeaconStateView
    {
        object State { get; }
        Validator[] Validators { get; }
        SyncCommittee CurrentSyncCommittee { get; }
        SyncCommittee NextSyncCommittee { get; }
        int[] GetActiveValidatorIndices(ulong epoch);
        Hash256 GetSeed(ulong epoch);
    }

    private sealed class FuluView(BeaconStateFulu state) : IBeaconStateView
    {
        public object State => state;
        public Validator[] Validators => state.Validators!;
        public SyncCommittee CurrentSyncCommittee => state.CurrentSyncCommittee!;
        public SyncCommittee NextSyncCommittee => state.NextSyncCommittee!;
        public int[] GetActiveValidatorIndices(ulong epoch) => state.GetActiveValidatorIndices(epoch);
        public Hash256 GetSeed(ulong epoch) => state.GetSeed(epoch, DomainType.SyncCommittee);
    }

    private sealed class GloasView(BeaconStateGloas state) : IBeaconStateView
    {
        public object State => state;
        public Validator[] Validators => state.Validators!;
        public SyncCommittee CurrentSyncCommittee => state.CurrentSyncCommittee!;
        public SyncCommittee NextSyncCommittee => state.NextSyncCommittee!;
        public int[] GetActiveValidatorIndices(ulong epoch) => state.GetActiveValidatorIndices(epoch);
        public Hash256 GetSeed(ulong epoch) => state.GetSeed(epoch, DomainType.SyncCommittee);
    }

    private static IBeaconStateView FuluState(ulong epoch, bool unequalBalances) => new FuluView(CreateState(epoch, LastSlotOf(epoch), unequalBalances));

    private static IBeaconStateView GloasState(ulong epoch, bool unequalBalances)
    {
        BeaconStateGloas state = GloasForkTransition.UpgradeToGloas(CreateState(epoch, 0, unequalBalances), SyntheticSpec());
        state.Slot = LastSlotOf(epoch);
        return new GloasView(state);
    }

    private static ulong LastSlotOf(ulong epoch) => (epoch + 1) * Presets.SlotsPerEpoch - 1;

    /// <summary>A Fulu state at <paramref name="slot"/> whose validators hold real keys, with a few leaving or joining at the epoch after <paramref name="epoch"/>.</summary>
    /// <param name="unequalBalances">Gives validator <c>i</c> an effective balance of <c>(i % 4) * 600</c> ETH instead of 32 ETH.</param>
    private static BeaconStateFulu CreateState(ulong epoch, ulong slot, bool unequalBalances)
    {
        BeaconStateFulu state = CreateFuluState(ValidatorCount);
        Validator[] validators = state.Validators!;
        ulong nextEpoch = epoch + 1;
        for (int i = 0; i < validators.Length; i++)
        {
            validators[i].Pubkey = new BlsPublicKey(new Bls.P1(ValidatorKey(i)).Compress());
            if (unequalBalances)
                validators[i].EffectiveBalance = (ulong)(i % 4) * 600 * Presets.EffectiveBalanceIncrement;
            if (Array.IndexOf(ExitingAtNextEpoch, i) >= 0)
                validators[i].ExitEpoch = nextEpoch;
            if (Array.IndexOf(ActivatingAtNextEpoch, i) >= 0)
                validators[i].ActivationEpoch = nextEpoch;
        }
        state.Slot = slot;
        return state;
    }
}
