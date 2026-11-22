// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using System.Reflection;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public class FastConfirmationTests
{
    [Test]
    public void Reading_confirmed_balances_preserves_the_heads_shared_validator_registry()
    {
        EquivocationFixture fixture = new();
        BeaconStateFulu state = fixture.StateA.Clone();
        ForkableArray<Validator>? shared = state.ValidatorBuffer;
        fixture.Source(state);
        Assert.That(state.ValidatorBuffer, Is.SameAs(shared));
    }

    [Test]
    public void Cached_scores_track_votes_independently_of_head_updates_and_survive_reset()
    {
        BeaconChainSpec spec = BeaconChainSpec.Mainnet;
        CheckpointRef anchor = new(0, GetRoot(1));
        ForkChoiceStore store = new(spec.SlotsPerEpoch, 64, anchor, anchor);
        ProtoArrayForkChoice tree = new(64, 0, Hash256.Zero, anchor, anchor, ExecutionStatus.Optimistic, anchor.Root);
        for (ulong root = 2; root <= 3; root++)
            tree.ProcessBlock(new ProtoBlock(1, GetRoot(root), anchor.Root, Hash256.Zero, anchor, anchor,
                ExecutionStatus.Optimistic, GetRoot(root), anchor, anchor), 64, anchor, anchor);
        ForkedBeaconState source = State(0, activation: false, laterEpoch: false);
        HashSet<ulong> equivocators = [];
        FastConfirmation rule = new(spec, store, tree, equivocators, _ => source, _ => source, (_, _) => source);
        Type sourceType = typeof(FastConfirmation).GetNestedType("BalanceSource", BindingFlags.NonPublic)!;
        object balanceSource = Activator.CreateInstance(sourceType, BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, args: [source, spec], culture: null)!;
        MethodInfo calculate = typeof(FastConfirmation).GetMethod("Scores", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ulong balance = 32 * Presets.EffectiveBalanceIncrement;
        tree.ProcessAttestation(0, GetRoot(2), 1);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(Calculate(), Is.EqualTo(new ulong[] { balance, balance, 0 }));
        tree.ProcessAttestation(0, GetRoot(3), 2);
        tree.GetHead(anchor, anchor, JustifiedBalances.FromEffectiveBalances([balance]), equivocators, 64);
        Assert.That(Calculate(), Is.EqualTo(new ulong[] { balance, 0, balance }));
        equivocators.Add(0);
        Assert.That(Calculate(), Is.EqualTo(new ulong[] { 0, 0, 0 }));
        equivocators.Clear();
        Assert.That(Calculate(), Is.EqualTo(new ulong[] { balance, 0, balance }));
        rule.Reset();
        tree.ProcessAttestation(0, GetRoot(2), 3);
        Assert.That(Calculate(), Is.EqualTo(new ulong[] { balance, balance, 0 }));
        tree.ProcessAttestation(0, GetRoot(3), 4);
        Assert.That(Calculate(), Is.EqualTo(new ulong[] { balance, 0, balance }));

        ulong[] Calculate() => (ulong[])calculate.Invoke(rule, [balanceSource])!;
    }

    [Test]
    public void Slashed_or_previous_epoch_votes_supply_no_support_but_remain_in_total_active_balance([Values] bool previousEpoch)
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
            source.State.Validators![i].Slashed = !previousEpoch && i < 24;
            tree.ProcessAttestation(i, GetRoot(2), previousEpoch ? 0UL : 1UL);
        }
        FastConfirmation rule = new(spec, store, tree, new HashSet<ulong>(), _ => source, _ => source, (_, _) => source);

        rule.OnSlot(GetRoot(2));

        Assert.That(rule.ConfirmedRoot, Is.EqualTo(previousEpoch ? GetRoot(2) : finalized.Root));
    }

    [Test]
    public void A_current_epoch_vote_on_a_pruned_orphan_supplies_no_checkpoint_support()
    {
        BeaconChainSpec spec = BeaconChainSpec.Mainnet with { SlotsPerEpoch = Presets.SlotsPerEpoch };
        CheckpointRef initial = new(0, GetRoot(1));
        CheckpointRef finalized = new(1, GetRoot(3));
        ForkChoiceStore store = new(spec.SlotsPerEpoch, spec.SlotsPerEpoch * 3 - 1, finalized, finalized);
        ProtoArrayForkChoice tree = new(store.CurrentSlot, 0, Hash256.Zero, initial, initial, ExecutionStatus.Valid, initial.Root)
        {
            PruneThreshold = 0,
        };
        Add(2, 1, spec.SlotsPerEpoch - 1);
        Add(3, 2, spec.SlotsPerEpoch);
        Add(5, 2, spec.SlotsPerEpoch * 2 + 1);
        Add(4, 3, spec.SlotsPerEpoch * 2 + 1);
        tree.ProcessAttestation(0, GetRoot(5), 2);
        tree.MaybePrune(finalized.Root);
        Assert.That(tree.GetParentRoot(GetRoot(5)), Is.Null, "fixture: the competing block lost its parent");
        ForkedBeaconState source = State(spec.SlotsPerEpoch, activation: false, laterEpoch: false);
        FastConfirmation rule = new(spec, store, tree, new HashSet<ulong>(), _ => source, _ => source, (_, _) => source);

        Assert.That(() => rule.OnSlot(GetRoot(4)), Throws.Nothing);
        Assert.That(rule.ConfirmedRoot, Is.EqualTo(finalized.Root));

        void Add(ulong root, ulong parent, ulong slot) => tree.ProcessBlock(new ProtoBlock(slot, GetRoot(root), GetRoot(parent),
            Hash256.Zero, finalized, finalized, ExecutionStatus.Valid, GetRoot(root), finalized, finalized), store.CurrentSlot, initial, initial);
    }

    /// <remarks>
    /// 10 votes of 32 ETH and one of 1,317 ETH score 1,637 ETH. The safety threshold is about 1,636.35 ETH when the proposer score leaves
    /// the 8 slashed validators out (<c>calculate_committee_fraction</c>), and about 1,637.95 ETH when it counts them.
    /// </remarks>
    [Test]
    public void Proposer_score_leaves_slashed_validators_out_of_the_safety_threshold()
    {
        BeaconChainSpec spec = BeaconChainSpec.Mainnet with { SlotsPerEpoch = Presets.SlotsPerEpoch };
        CheckpointRef finalized = new(1, GetRoot(1));
        ForkChoiceStore store = new(spec.SlotsPerEpoch, spec.SlotsPerEpoch * 2 - 1, finalized, finalized);
        ProtoArrayForkChoice tree = new(store.CurrentSlot, spec.SlotsPerEpoch, Hash256.Zero, finalized, finalized,
            ExecutionStatus.Valid, GetRoot(1));
        tree.ProcessBlock(new ProtoBlock(spec.SlotsPerEpoch + 1, GetRoot(2), finalized.Root, Hash256.Zero,
            finalized, finalized, ExecutionStatus.Valid, GetRoot(2), finalized, finalized), store.CurrentSlot, finalized, finalized);
        ForkedBeaconState.OfFulu source = (ForkedBeaconState.OfFulu)State(spec.SlotsPerEpoch, activation: false, laterEpoch: false);
        Validator[] validators = source.State.Validators!;
        for (ulong i = 0; i < 8; i++) validators[i].Slashed = true;
        for (ulong i = 8; i < 18; i++) tree.ProcessAttestation(i, GetRoot(2), 1);
        validators[31].EffectiveBalance = 1_317 * Presets.EffectiveBalanceIncrement;
        tree.ProcessAttestation(31, GetRoot(2), 1);
        FastConfirmation rule = new(spec, store, tree, new HashSet<ulong>(), _ => source, _ => source, (_, _) => source);

        rule.OnSlot(GetRoot(2));

        Assert.That(rule.ConfirmedRoot, Is.EqualTo(GetRoot(2)));
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


    [Test]
    public void Ineligible_equivocators_do_not_require_a_committee_state([Values] bool zeroBalance)
    {
        EquivocationFixture fixture = new(failCommitteeState: true);
        if (zeroBalance)
        {
            foreach (ulong index in fixture.Equivocators)
                if (index < (ulong)fixture.StateA.Validators!.Length) fixture.StateA.Validators[index].EffectiveBalance = 0;
        }
        else
        {
            fixture.Equivocators.Clear();
            fixture.Equivocators.Add(1000);
        }
        ulong end = fixture.Spec.SlotsPerEpoch - 1;
        ulong expected = fixture.Reference(fixture.StateA, 0, end);
        Assert.That(fixture.Weight(fixture.Context(fixture.HeadA), fixture.Source(fixture.StateA), 0, end), Is.EqualTo(expected));
    }

    [Test]
    public void Equivocation_prefixes_match_committee_enumeration_for_each_balance_source()
    {
        EquivocationFixture fixture = new();
        BeaconStateFulu second = ((ForkedBeaconState.OfFulu)State(0, activation: false, laterEpoch: true)).State;
        second.Validators![1].EffectiveBalance = 0;
        second.Validators[2].Slashed = true;
        second.Validators[3].ExitEpoch = 0;
        object context = fixture.Context(fixture.HeadA);
        object firstSource = fixture.Source(fixture.StateA);
        object secondSource = fixture.Source(second);
        using IDisposable scope = Assert.EnterMultipleScope();
        for (ulong start = 0; start < fixture.Spec.SlotsPerEpoch; start++)
        {
            for (ulong end = start; end < fixture.Spec.SlotsPerEpoch; end++)
            {
                Assert.That(fixture.Weight(context, firstSource, start, end), Is.EqualTo(fixture.Reference(fixture.StateA, start, end)));
                Assert.That(fixture.Weight(context, secondSource, start, end), Is.EqualTo(fixture.Reference(second, start, end)));
            }
        }
    }

    [Test]
    public void Cross_epoch_equivocation_counts_each_validator_once_and_new_evaluations_refresh_the_set()
    {
        EquivocationFixture fixture = new();
        object source = fixture.Source(fixture.StateA);
        ulong end = 2 * fixture.Spec.SlotsPerEpoch - 1;
        using IDisposable scope = Assert.EnterMultipleScope();
        foreach (ulong start in new ulong[] { 0, fixture.Spec.SlotsPerEpoch / 2, fixture.Spec.SlotsPerEpoch - 1 })
            Assert.That(fixture.Weight(fixture.Context(fixture.HeadA), source, start, end), Is.EqualTo(fixture.Reference(fixture.StateA, start, end)));
        object first = fixture.Context(fixture.HeadA);
        Assert.That(fixture.Weight(first, source, 0, fixture.Spec.SlotsPerEpoch - 1), Is.EqualTo(fixture.Reference(fixture.StateA, 0, fixture.Spec.SlotsPerEpoch - 1)));
        fixture.Equivocators.Clear();
        fixture.Equivocators.Add(7);
        Assert.That(fixture.Weight(fixture.Context(fixture.HeadA), source, 0, fixture.Spec.SlotsPerEpoch - 1), Is.EqualTo(fixture.Reference(fixture.StateA, 0, fixture.Spec.SlotsPerEpoch - 1)));
        Assert.That(fixture.Weight(first, source, 1, 0), Is.Zero);
    }

    [Test]
    public void Duty_tables_follow_the_committee_decision_root_and_its_cache_lifetime()
    {
        EquivocationFixture fixture = new(stateEpoch: 9);
        int[] original = fixture.Duties(fixture.Context(fixture.HeadA), 3);
        int[] competing = fixture.Duties(fixture.Context(fixture.HeadB), 3);
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(fixture.Duties(fixture.Context(fixture.HeadA), 3), Is.SameAs(original));
        Assert.That(competing, Is.Not.SameAs(original));
        Assert.That(competing, Is.Not.EqualTo(original));
        fixture.Rule.Reset();
        int[] reset = fixture.Duties(fixture.Context(fixture.HeadA), 3);
        Assert.That(reset, Is.EqualTo(original).And.Not.SameAs(original));
        for (ulong epoch = 4; epoch <= 9; epoch++) fixture.Duties(fixture.Context(fixture.HeadA), epoch);
        Assert.That(fixture.Duties(fixture.Context(fixture.HeadA), 3), Is.EqualTo(original).And.Not.SameAs(reset));
    }

    private sealed class EquivocationFixture
    {
        internal readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet with { SlotsPerEpoch = Presets.SlotsPerEpoch };
        internal readonly HashSet<ulong> Equivocators = [1, 2, 3, 1000];
        internal readonly Hash256 HeadA = GetRoot(3);
        internal readonly Hash256 HeadB = GetRoot(5);
        internal readonly BeaconStateFulu StateA;
        internal readonly FastConfirmation Rule;
        private readonly Dictionary<ulong, CommitteeCache> _referenceCommittees = [];
        private static readonly Type SourceType = typeof(FastConfirmation).GetNestedType("BalanceSource", BindingFlags.NonPublic)!;
        private static readonly Type ContextType = typeof(FastConfirmation).GetNestedType("Context", BindingFlags.NonPublic)!;
        private static readonly MethodInfo WeightMethod = ContextType.GetMethod("AdversarialWeight", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly MethodInfo DutiesMethod = ContextType.GetMethod("Duties", BindingFlags.NonPublic | BindingFlags.Instance)!;

        internal EquivocationFixture(bool failCommitteeState = false, ulong stateEpoch = 0)
        {
            StateA = ((ForkedBeaconState.OfFulu)State(stateEpoch * Spec.SlotsPerEpoch, activation: false, laterEpoch: false)).State;
            BeaconStateFulu stateB = ((ForkedBeaconState.OfFulu)State(stateEpoch * Spec.SlotsPerEpoch, activation: false, laterEpoch: false)).State;
            Array.Fill(stateB.RandaoMixes!, GetRoot(99));
            CheckpointRef anchor = new(0, GetRoot(1));
            ForkChoiceStore store = new(Spec.SlotsPerEpoch, Spec.SlotsPerEpoch * 3 + 2, anchor, anchor);
            ProtoArrayForkChoice tree = new(store.CurrentSlot, 0, Hash256.Zero, anchor, anchor, ExecutionStatus.Optimistic, anchor.Root);
            Add(2, 1, Spec.SlotsPerEpoch * 2 - 1);
            Add(3, 2, Spec.SlotsPerEpoch * 3 + 1);
            Add(4, 1, Spec.SlotsPerEpoch * 2 - 1);
            Add(5, 4, Spec.SlotsPerEpoch * 3 + 1);
            Rule = new FastConfirmation(Spec, store, tree, Equivocators, _ => new ForkedBeaconState.OfFulu(StateA),
                root => failCommitteeState ? throw new ForkChoiceException("Committee state unavailable")
                    : new ForkedBeaconState.OfFulu(root == HeadB ? stateB : StateA), (_, _) => new ForkedBeaconState.OfFulu(StateA));

            void Add(ulong root, ulong parent, ulong slot) => tree.ProcessBlock(new ProtoBlock(slot, GetRoot(root), GetRoot(parent),
                Hash256.Zero, anchor, anchor, ExecutionStatus.Optimistic, GetRoot(root), anchor, anchor), store.CurrentSlot, anchor, anchor);
        }

        internal object Source(BeaconStateFulu state) => Activator.CreateInstance(SourceType, BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, args: [new ForkedBeaconState.OfFulu(state), Spec], culture: null)!;
        internal object Context(Hash256 head) => Activator.CreateInstance(ContextType, [Rule, head])!;
        internal ulong Weight(object context, object source, ulong start, ulong end) => (ulong)WeightMethod.Invoke(context, [source, start, end])!;
        internal int[] Duties(object context, ulong epoch) => (int[])DutiesMethod.Invoke(context, [epoch])!;

        internal ulong Reference(BeaconStateFulu source, ulong start, ulong end)
        {
            ulong total = 0;
            foreach (Validator validator in source.Validators!)
                if (validator.IsActiveValidator(source.Slot / Spec.SlotsPerEpoch)) total += validator.EffectiveBalance;
            ulong equivocation = 0;
            foreach (ulong index in Equivocators)
            {
                if (index >= (ulong)source.Validators.Length || !source.Validators[index].IsActiveValidator(source.Slot / Spec.SlotsPerEpoch)) continue;
                for (ulong slot = start; slot <= end; slot++)
                {
                    ulong epoch = slot / Spec.SlotsPerEpoch;
                    if (!_referenceCommittees.TryGetValue(epoch, out CommitteeCache? committees))
                        _referenceCommittees[epoch] = committees = CommitteeCache.Build(StateA, epoch);
                    bool found = false;
                    for (int committee = 0; committee < committees.CommitteesPerSlot; committee++)
                        found |= committees.GetBeaconCommittee(slot, committee).Contains((int)index);
                    if (!found) continue;
                    equivocation += source.Validators[index].EffectiveBalance;
                    break;
                }
            }
            ulong maximum = FastConfirmation.EstimateCommitteeWeight(total, start, end, Spec.SlotsPerEpoch) / 100 * 25;
            return maximum > equivocation ? maximum - equivocation : 0;
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
