// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public class FastConfirmationTests
{
    [Test]
    public void Slashed_validators_supply_no_support_but_remain_in_total_active_balance()
    {
        BeaconChainSpec spec = BeaconChainSpec.Mainnet with { SlotsPerEpoch = Presets.SlotsPerEpoch };
        CheckpointRef finalized = new(1, GetRoot(1));
        ForkChoiceStore store = new(spec.SlotsPerEpoch, spec.SlotsPerEpoch * 2 - 1, finalized, finalized);
        ProtoArrayForkChoice tree = new(store.CurrentSlot, spec.SlotsPerEpoch, Hash256.Zero, finalized, finalized,
            ExecutionStatus.Valid, GetRoot(1));
        tree.ProcessBlock(new ProtoBlock(spec.SlotsPerEpoch + 1, GetRoot(2), finalized.Root, Hash256.Zero,
            finalized, finalized, ExecutionStatus.Valid, GetRoot(2), finalized, finalized), store.CurrentSlot, finalized, finalized);
        ForkedBeaconState.OfFulu source = (ForkedBeaconState.OfFulu)State(spec.SlotsPerEpoch, activation: false, laterEpoch: false);
        for (ulong i = 0; i < 32; i++)
        {
            source.State.Validators![i].Slashed = i < 24;
            tree.ProcessAttestation(i, GetRoot(2), 1);
        }
        FastConfirmation rule = new(spec, store, tree, new HashSet<ulong>(), _ => source, _ => source, (_, _) => source);

        rule.OnSlot(GetRoot(2));

        Assert.That(rule.ConfirmedRoot, Is.EqualTo(finalized.Root), "the eight unslashed votes cannot exceed a threshold based on all 32 active validators");
    }

    [Test]
    public void Epoch_reconfirmation_uses_previous_balances_and_advancement_uses_current_balances([Values] bool activation)
    {
        BeaconChainSpec spec = BeaconChainSpec.Mainnet with { SlotsPerEpoch = Presets.SlotsPerEpoch };
        ulong slotsPerEpoch = spec.SlotsPerEpoch;
        CheckpointRef finalized = new(1, GetRoot(1));
        CheckpointRef nextJustified = new(2, GetRoot(3));
        ForkChoiceStore store = new(slotsPerEpoch, slotsPerEpoch * 2 - 1, finalized, finalized);
        ProtoArrayForkChoice tree = new(store.CurrentSlot, slotsPerEpoch, Hash256.Zero, finalized, finalized,
            ExecutionStatus.Valid, GetRoot(1));
        Dictionary<Hash256, ForkedBeaconState> states = [];
        states[GetRoot(1)] = State(slotsPerEpoch, activation, laterEpoch: false);
        states[GetRoot(2)] = State(slotsPerEpoch + 1, activation, laterEpoch: false);
        states[GetRoot(3)] = State(slotsPerEpoch * 2, activation, laterEpoch: true);
        states[GetRoot(4)] = State(slotsPerEpoch * 2 + 1, activation, laterEpoch: true);
        states[GetRoot(5)] = State(slotsPerEpoch * 2 + 3, activation, laterEpoch: true);
        FastConfirmation rule = new(spec, store, tree, new HashSet<ulong>(),
            checkpoint => states[checkpoint.Root], root => states[root], (root, _) => states[root]);

        AddBlock(2, 1, slotsPerEpoch + 1, finalized);
        Vote(2, 1);
        rule.OnSlot(GetRoot(2));
        Assert.That(rule.ConfirmedRoot, Is.EqualTo(GetRoot(2)), "24 of the 32 validators active at the observed checkpoint support the block");

        AddBlock(3, 2, slotsPerEpoch * 2, nextJustified);
        AddBlock(4, 3, slotsPerEpoch * 2 + 1, nextJustified);
        store.OnTick(slotsPerEpoch * 2);
        rule.OnSlot(GetRoot(3));
        Vote(4, 2);
        int validatorCount = activation ? 64 : 32;
        for (ulong i = 24; i < (ulong)validatorCount; i++)
            tree.ProcessAttestation(i, GetRoot(3), 2);
        store.UpdateUnrealizedCheckpoints(nextJustified, finalized);
        store.OnTick(slotsPerEpoch * 3 - 1);
        rule.OnSlot(GetRoot(4));
        Assert.That(rule.ConfirmedRoot, Is.EqualTo(GetRoot(4)), "new activations or balance changes in the head cannot replace the observed checkpoint's balance source");

        AddBlock(5, 4, slotsPerEpoch * 2 + 3, nextJustified);
        Vote(5, 3);
        store.OnTick(slotsPerEpoch * 3);
        rule.OnSlot(GetRoot(5));

        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(rule.PreviousEpochObservedJustifiedCheckpoint, Is.EqualTo(finalized));
        Assert.That(rule.CurrentEpochObservedJustifiedCheckpoint, Is.EqualTo(nextJustified));
        Assert.That(rule.ConfirmedRoot, Is.EqualTo(GetRoot(4)), "reconfirmation retains the old confirmation using previous balances, while current balances refuse its child");

        void AddBlock(ulong root, ulong parent, ulong slot, CheckpointRef unrealized) => tree.ProcessBlock(new ProtoBlock(
            slot, GetRoot(root), GetRoot(parent), Hash256.Zero, finalized, finalized, ExecutionStatus.Valid,
            GetRoot(root), unrealized, finalized), store.CurrentSlot, finalized, finalized);

        void Vote(ulong root, ulong epoch)
        {
            for (ulong i = 0; i < 24; i++) tree.ProcessAttestation(i, GetRoot(root), epoch);
        }
    }

    private static ForkedBeaconState State(ulong slot, bool activation, bool laterEpoch)
    {
        Validator[] validators = new Validator[activation ? 64 : 32];
        byte[] credentials = new byte[32];
        credentials[0] = Presets.CompoundingWithdrawalPrefix;
        for (int i = 0; i < validators.Length; i++)
        {
            ulong balance = laterEpoch && !activation ? i < 24 ? 16UL : 128UL : 32UL;
            validators[i] = new Validator
            {
                EffectiveBalance = balance * Presets.EffectiveBalanceIncrement,
                ActivationEpoch = i < 32 ? 0UL : 2UL,
                ExitEpoch = Presets.FarFutureEpoch,
                WithdrawalCredentials = new Hash256(credentials),
            };
        }
        Hash256[] mixes = new Hash256[Presets.EpochsPerHistoricalVector];
        Array.Fill(mixes, Hash256.Zero);
        return new ForkedBeaconState.OfFulu(new BeaconStateFulu { Slot = slot, Validators = validators, RandaoMixes = mixes });
    }
}
