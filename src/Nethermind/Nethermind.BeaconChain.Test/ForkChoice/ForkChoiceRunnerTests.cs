// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using FuluStateTransition = Nethermind.BeaconChain.StateTransition.StateTransition;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>
/// The pure predicates behind <see cref="ForkChoiceRunner.ShouldOverrideForkchoiceUpdate"/> (silent-wrong
/// risks: get_proposer_head returning the ordinary head instead of ever re-org'ing), and the way
/// <see cref="ForkChoiceRunner.OnBlock"/> hands <c>is_data_available</c> to whichever
/// <see cref="IDataAvailabilityRule"/> its caller chose: a column list means the supernode rule the
/// spec vectors need, a rule means exactly that rule, and neither is ever inferred from the other.
/// </summary>
public class ForkChoiceRunnerTests
{
    [TestCase(0ul, false, TestName = "epoch_boundary_slot_is_unstable")]
    [TestCase(1ul, true, TestName = "mid_epoch_slot_is_stable")]
    [TestCase(31ul, true, TestName = "last_slot_of_epoch_is_stable")]
    [TestCase(32ul, false, TestName = "next_epoch_boundary_is_unstable")]
    public void IsShufflingStable_is_false_only_at_an_epoch_boundary(ulong slot, bool expected) =>
        Assert.That(ForkChoiceRunner.IsShufflingStable(slot, slotsPerEpoch: 32), Is.EqualTo(expected));

    [TestCase(0ul, 0ul, true, TestName = "same_epoch_is_ok")]
    [TestCase(64ul, 0ul, true, TestName = "exactly_the_max_gap_is_ok")]
    [TestCase(96ul, 0ul, false, TestName = "one_epoch_past_the_max_gap_is_not_ok")]
    public void IsFinalizationOk_allows_up_to_the_configured_epoch_gap(ulong slot, ulong finalizedEpoch, bool expected) =>
        Assert.That(ForkChoiceRunner.IsFinalizationOk(slot, finalizedEpoch, reorgMaxEpochsSinceFinalization: 2), Is.EqualTo(expected));

    // finalizedEpoch (5) can never legitimately exceed compute_epoch_at_slot(slot) (0) in a consistent
    // store; the ulong subtraction underflows to a huge gap, which must deny the reorg rather than
    // wrap around into a false "ok".
    [Test]
    public void IsFinalizationOk_fails_closed_if_finalized_epoch_somehow_outran_the_slot() =>
        Assert.That(ForkChoiceRunner.IsFinalizationOk(slot: 0, finalizedEpoch: 5, reorgMaxEpochsSinceFinalization: 2), Is.False);

    [TestCase(1ul, 2ul, 3ul, true, TestName = "consecutive_parent_head_and_proposal_slots")]
    [TestCase(0ul, 2ul, 3ul, false, TestName = "parent_is_not_immediately_before_head")]
    [TestCase(1ul, 2ul, 4ul, false, TestName = "head_is_not_immediately_before_the_proposal_slot")]
    public void IsSingleSlotReorg_requires_three_consecutive_slots(ulong parentSlot, ulong headSlot, ulong proposalSlot, bool expected) =>
        Assert.That(ForkChoiceRunner.IsSingleSlotReorg(parentSlot, headSlot, proposalSlot), Is.EqualTo(expected));

    [Test]
    public void OnBlock_with_a_column_list_applies_the_supernode_rule_regardless_of_this_nodes_custody()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        (ForkChoiceRunner runner, BeaconStateFulu postState) = RunnerAt(chain);
        // The eight columns a base-custody node would hold: enough for the production rule, never for this one.
        DataColumnSidecar[] eightColumns = [.. chain.Columns.Take(8)];

        Assert.Multiple(() =>
        {
            Assert.That(() => runner.OnBlock(chain.Block, postState, ExecutionStatus.Valid, eightColumns),
                Throws.TypeOf<ForkChoiceException>().With.Message.Contains("blob data"));
            Assert.That(runner.ContainsBlock(chain.BlockRoot), Is.False, "a rejected block must not have been added to the tree");
            Assert.That(() => runner.OnBlock(chain.Block, postState, ExecutionStatus.Valid, chain.Columns), Throws.Nothing,
                "the spec vectors hand over the whole matrix and expect acceptance");
            Assert.That(runner.ContainsBlock(chain.BlockRoot), Is.True);
        });
    }

    [Test]
    public void OnBlock_with_a_rule_asks_that_rule_about_this_block_and_honors_its_verdict()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        (ForkChoiceRunner runner, BeaconStateFulu postState) = RunnerAt(chain);
        RecordingRule refusing = new(verdict: false);
        RecordingRule accepting = new(verdict: true);

        Assert.Multiple(() =>
        {
            Assert.That(() => runner.OnBlock(chain.Block, postState, ExecutionStatus.Valid, refusing), Throws.TypeOf<ForkChoiceException>());
            Assert.That(refusing.Asked, Is.EqualTo(new List<(BeaconBlock, Hash256)> { (chain.Block.Message!, chain.BlockRoot) }),
                "the rule is consulted for this block, keyed by its real root");
            Assert.That(runner.ContainsBlock(chain.BlockRoot), Is.False);

            Assert.That(() => runner.OnBlock(chain.Block, postState, ExecutionStatus.Valid, accepting), Throws.Nothing);
            Assert.That(accepting.Asked, Has.Count.EqualTo(1));
            Assert.That(runner.ContainsBlock(chain.BlockRoot), Is.True);
        });
    }

    /// <summary>A runner rooted at the fixture's anchor, ticked to the block's slot, with the block's real post-state computed.</summary>
    private static (ForkChoiceRunner Runner, BeaconStateFulu PostState) RunnerAt(ImportableBlobBlock chain)
    {
        InMemoryStates states = new();
        states.States[chain.AnchorRoot] = chain.AnchorState;
        ForkChoiceRunner runner = new(chain.Spec, chain.AnchorState, chain.AnchorBlock.Message!, states, chain.Pubkeys);
        runner.OnTick(runner.GenesisTime + chain.Block.Message!.Slot * chain.Spec.SecondsPerSlot);

        BeaconStateFulu postState = chain.AnchorState.Clone();
        FuluStateTransition.Apply(postState, chain.Block, new EpochCache(), chain.Pubkeys, new AcceptingNotifier(), chain.Spec);
        return (runner, postState);
    }

    private sealed class RecordingRule(bool verdict) : IDataAvailabilityRule
    {
        public List<(BeaconBlock Block, Hash256 Root)> Asked { get; } = [];

        public bool IsDataAvailable(BeaconBlock block, Hash256 blockRoot, BeaconChainSpec spec)
        {
            Asked.Add((block, blockRoot));
            return verdict;
        }
    }

    private sealed class InMemoryStates : IForkChoiceStateProvider
    {
        public Dictionary<Hash256, BeaconStateFulu> States { get; } = [];

        public BeaconStateFulu? GetBlockState(Hash256 blockRoot) => States.GetValueOrDefault(blockRoot);

        public BeaconStateFulu? CopyBlockState(Hash256 blockRoot) => GetBlockState(blockRoot)?.Clone();
    }

    private sealed class AcceptingNotifier : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }
}
