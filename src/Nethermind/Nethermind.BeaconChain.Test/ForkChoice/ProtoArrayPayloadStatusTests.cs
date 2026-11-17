// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.ForkChoice.ProtoArrayTestBlocks;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public class ProtoArrayPayloadStatusTests
{
    private const ulong SlotsPerEpoch = 32;

    private static readonly CheckpointRef Anchor = new(0, GetRoot(0));
    private static readonly Hash256 OtherHash = new("0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

    [Test]
    public void Gloas_filter_ancestry_matches_individual_queries_after_growth_finality_and_pruning([Values(64, 1024)] int length)
    {
        ProtoArray tree = new(SlotsPerEpoch, 40) { PruneThreshold = 0 };
        HashSet<Hash256> payloads = [];
        AddThrough(length);
        Check(new CheckpointRef(1, GetRoot(32)));
        Check(new CheckpointRef(2, GetRoot(64)));
        Check(new CheckpointRef(1, OtherHash));
        Check(Anchor);
        AddThrough(length + 64);
        Check(new CheckpointRef(1, GetRoot(32)));
        tree.Nodes[^1].ExecutionStatus = ExecutionStatus.Invalid;
        tree.Prune(GetRoot(32));
        Check(new CheckpointRef(2, GetRoot(64)));

        void AddThrough(int last)
        {
            for (int i = tree.Nodes.Count; i <= last; i++)
            {
                Hash256 root = GetRoot((ulong)i);
                Hash256? parent = i == 0 ? null : GetRoot((ulong)i - 1);
                tree.OnBlock(Block((ulong)i, root, parent, isGloas: true, parentBlockHash: parent), (ulong)last, Anchor, Anchor);
                payloads.Add(root);
            }
        }

        void Check(CheckpointRef finalized)
        {
            bool[,] filtered = tree.FilterNodeTree((ulong)length + 64, Anchor, finalized, payloads);
            using System.IDisposable scope = Assert.EnterMultipleScope();
            for (int i = 0; i < tree.Nodes.Count; i++)
            {
                ProtoNode node = tree.Nodes[i];
                bool expected = node.ExecutionStatus != ExecutionStatus.Invalid
                    && (finalized.Epoch == 0 || tree.IsFinalizedCheckpointOrDescendant(node.Root, finalized));
                Assert.That(filtered[i, (int)ForkChoicePayloadStatus.Empty], Is.EqualTo(expected), $"node {i}, finalized {finalized}");
            }
        }
    }

    [Test]
    public void Gloas_filter_does_not_propagate_a_parents_checkpoint_shortcut([Range(0, 3)] int shortcut)
    {
        CheckpointRef finalized = new(1, OtherHash);
        ProtoArray tree = new(SlotsPerEpoch, 40);
        tree.OnBlock(Block(0, GetRoot(0), null, isGloas: true), 35, Anchor, Anchor);
        ProtoBlock parent = Block(33, GetRoot(1), GetRoot(0), isGloas: true, parentBlockHash: GetRoot(0));
        parent = shortcut switch
        {
            0 => parent with { FinalizedCheckpoint = finalized },
            1 => parent with { JustifiedCheckpoint = finalized },
            2 => parent with { UnrealizedFinalizedCheckpoint = finalized },
            _ => parent with { UnrealizedJustifiedCheckpoint = finalized },
        };
        tree.OnBlock(parent, 35, Anchor, Anchor);
        tree.OnBlock(Block(35, GetRoot(2), GetRoot(1), isGloas: true, parentBlockHash: GetRoot(1)), 35, Anchor, Anchor);

        bool[,] filtered = tree.FilterNodeTree(35, Anchor, finalized, new HashSet<Hash256> { GetRoot(0), GetRoot(1), GetRoot(2) });

        using System.IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(filtered[1, (int)ForkChoicePayloadStatus.Empty], Is.True);
        Assert.That(filtered[2, (int)ForkChoicePayloadStatus.Empty], Is.False);
    }

    [Test]
    public void Gloas_filter_preserves_a_mid_epoch_bootstrap_checkpoint()
    {
        CheckpointRef bootstrap = new(1, GetRoot(0));
        ProtoArray tree = new(SlotsPerEpoch, 40);
        tree.OnBlock(Block(33, GetRoot(0), null, isGloas: true) with { FinalizedCheckpoint = bootstrap }, 35, Anchor, bootstrap);
        tree.OnBlock(Block(35, GetRoot(1), GetRoot(0), isGloas: true, parentBlockHash: GetRoot(0)), 35, Anchor, bootstrap);

        bool[,] filtered = tree.FilterNodeTree(35, Anchor, bootstrap, new HashSet<Hash256> { GetRoot(0), GetRoot(1) });

        Assert.That(filtered[1, (int)ForkChoicePayloadStatus.Empty], Is.True);
    }

    private static ProtoBlock Block(ulong slot, Hash256 root, Hash256? parent, ExecutionStatus status = ExecutionStatus.Optimistic, bool isGloas = false, Hash256? parentBlockHash = null) =>
        CreateProtoBlock(slot, root, parent, Anchor, Anchor, status, root,
            isGloas: isGloas, parentBlockHash: parentBlockHash);

    private static ProtoArrayForkChoice NewForkChoice(bool gloasAnchor = false) =>
        new(0, 0, Hash256.Zero, Anchor, Anchor, ExecutionStatus.Optimistic, GetRoot(0), SlotsPerEpoch, isGloas: gloasAnchor);

    private static ProtoNode Node(ProtoArrayForkChoice fc, Hash256 root) => fc.Nodes.Single(n => n.Root == root);

    private static void Settle(ProtoArrayForkChoice fc, ulong[] balances, ulong slot, Hash256? boost = null)
    {
        fc.SetProposerBoostRoot(boost ?? Hash256.Zero);
        fc.GetHead(Anchor, Anchor, JustifiedBalances.FromEffectiveBalances(balances), null, slot);
    }

    [Test]
    public void Repeated_heads_apply_status_balance_and_equivocation_changes()
    {
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0), isGloas: true), 2, Anchor, Anchor);
        JustifiedBalances balances = JustifiedBalances.FromEffectiveBalances([7]);
        HashSet<ulong> equivocators = [];
        fc.ProcessAttestation(0, GetRoot(1), 2, 0, payloadPresent: true);
        Check(7, 0, 7);
        Check(7, 0, 7);
        fc.ProcessAttestation(0, GetRoot(1), 3, 0, payloadPresent: false);
        Check(7, 7, 0);
        Check(7, 7, 0);
        balances = JustifiedBalances.FromEffectiveBalances([11]);
        Check(11, 11, 0);
        equivocators.Add(0);
        Check(0, 0, 0);
        Check(0, 0, 0);
        equivocators.Clear();
        Check(11, 11, 0);

        void Check(ulong weight, ulong empty, ulong full)
        {
            fc.GetHead(Anchor, Anchor, balances, equivocators, 4);
            ProtoNode node = Node(fc, GetRoot(1));
            Assert.That((node.Weight, node.EmptyWeight, node.FullWeight), Is.EqualTo((weight, empty, full)));
        }
    }

    [TestCase(2ul, true, ForkChoicePayloadStatus.Full, TestName = "Later slot, index 1: FULL")]
    [TestCase(2ul, false, ForkChoicePayloadStatus.Empty, TestName = "Later slot, index 0: EMPTY")]
    [TestCase(1ul, false, ForkChoicePayloadStatus.Pending, TestName = "Block's own slot: PENDING")]
    [TestCase(1ul, true, ForkChoicePayloadStatus.Pending, TestName = "Block's own slot, payload flag ignored: PENDING")]
    [TestCase(2ul, null, ForkChoicePayloadStatus.Pending, TestName = "Pre-Gloas slot: PENDING")]
    public void Vote_counts_in_the_node_it_supports(ulong voteSlot, bool? payloadPresent, ForkChoicePayloadStatus supported)
    {
        const ulong balance = 7;
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0), isGloas: true, parentBlockHash: GetRoot(0)), 2, Anchor, Anchor);

        fc.ProcessAttestation(0, GetRoot(1), voteSlot, 0, payloadPresent);
        Settle(fc, [balance], 2);

        ProtoNode block = Node(fc, GetRoot(1));
        ProtoNode anchor = Node(fc, GetRoot(0));
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(block.Weight, Is.EqualTo(balance), "PENDING counts every supported node of the block");
        Assert.That(block.EmptyWeight, Is.EqualTo(supported == ForkChoicePayloadStatus.Empty ? balance : 0));
        Assert.That(block.FullWeight, Is.EqualTo(supported == ForkChoicePayloadStatus.Full ? balance : 0));
        Assert.That(anchor.FullWeight, Is.EqualTo(balance), "a child of a pre-Gloas block builds on it full");
        Assert.That(anchor.EmptyWeight, Is.Zero);
    }

    [Test]
    public void Votes_on_every_node_of_one_block_add_up_exactly()
    {
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0), isGloas: true, parentBlockHash: GetRoot(0)), 3, Anchor, Anchor);

        fc.ProcessAttestation(0, GetRoot(1), 2, 0, payloadPresent: true);
        fc.ProcessAttestation(1, GetRoot(1), 2, 0, payloadPresent: false);
        fc.ProcessAttestation(2, GetRoot(1), 1, 0, payloadPresent: false);
        fc.ProcessAttestation(3, GetRoot(1), 2, 0, payloadPresent: null);
        Settle(fc, [1, 2, 4, 8], 3);

        ProtoNode block = Node(fc, GetRoot(1));
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(block.FullWeight, Is.EqualTo(1ul));
        Assert.That(block.EmptyWeight, Is.EqualTo(2ul));
        Assert.That(block.Weight, Is.EqualTo(15ul));
    }

    [TestCase(2ul, 3ul, true, true, TestName = "Gloas: a later slot in the same epoch replaces")]
    [TestCase(3ul, 2ul, true, false, TestName = "Gloas: an earlier slot does not replace")]
    [TestCase(2ul, 2ul, true, false, TestName = "Gloas: the same slot does not replace")]
    [TestCase(2ul, 3ul, false, false, TestName = "Pre-Gloas: a later slot in the same epoch does not replace")]
    [TestCase(2ul, 33ul, false, true, TestName = "Pre-Gloas: a later epoch replaces")]
    public void Latest_message_replacement(ulong firstSlot, ulong secondSlot, bool gloas, bool replaced)
    {
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0), isGloas: gloas), 40, Anchor, Anchor);
        fc.ProcessBlock(Block(1, GetRoot(2), GetRoot(0), isGloas: gloas), 40, Anchor, Anchor);
        bool? payloadPresent = gloas ? false : null;

        fc.ProcessAttestation(0, GetRoot(1), firstSlot, firstSlot / SlotsPerEpoch, payloadPresent);
        fc.ProcessAttestation(0, GetRoot(2), secondSlot, secondSlot / SlotsPerEpoch, payloadPresent);
        Settle(fc, [5], 40);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(Node(fc, GetRoot(2)).Weight, Is.EqualTo(replaced ? 5ul : 0ul));
        Assert.That(Node(fc, GetRoot(1)).Weight, Is.EqualTo(replaced ? 0ul : 5ul));
    }

    [Test]
    public void Child_weight_lands_in_the_parent_node_it_builds_on()
    {
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0), isGloas: true, parentBlockHash: GetRoot(0)), 3, Anchor, Anchor);
        fc.ProcessBlock(Block(2, GetRoot(2), GetRoot(1), isGloas: true, parentBlockHash: GetRoot(1)), 3, Anchor, Anchor);
        fc.ProcessBlock(Block(2, GetRoot(3), GetRoot(1), isGloas: true, parentBlockHash: OtherHash), 3, Anchor, Anchor);

        fc.ProcessAttestation(0, GetRoot(2), 2, 0, payloadPresent: false);
        fc.ProcessAttestation(1, GetRoot(3), 2, 0, payloadPresent: false);
        Settle(fc, [3, 11], 3);

        ProtoNode parent = Node(fc, GetRoot(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Node(fc, GetRoot(2)).ParentPayloadStatus, Is.EqualTo(ForkChoicePayloadStatus.Full));
            Assert.That(Node(fc, GetRoot(3)).ParentPayloadStatus, Is.EqualTo(ForkChoicePayloadStatus.Empty));
            Assert.That(parent.FullWeight, Is.EqualTo(3ul));
            Assert.That(parent.EmptyWeight, Is.EqualTo(11ul));
            Assert.That(parent.Weight, Is.EqualTo(14ul));
        }

        fc.ProcessAttestation(0, GetRoot(3), 3, 0, payloadPresent: false);
        Settle(fc, [3, 11], 4);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(parent.FullWeight, Is.Zero);
        Assert.That(parent.EmptyWeight, Is.EqualTo(14ul));
        Assert.That(parent.Weight, Is.EqualTo(14ul));
    }

    [TestCase(false, ForkChoicePayloadStatus.Full, TestName = "Child of a pre-Gloas anchor builds on it full")]
    [TestCase(true, ForkChoicePayloadStatus.Empty, TestName = "Child of a Gloas anchor with another parent hash builds on it empty")]
    public void Parent_payload_status_of_an_anchor_child(bool gloasAnchor, ForkChoicePayloadStatus expected)
    {
        ProtoArrayForkChoice fc = NewForkChoice(gloasAnchor);
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0), isGloas: true, parentBlockHash: OtherHash), 1, Anchor, Anchor);

        Assert.That(Node(fc, GetRoot(1)).ParentPayloadStatus, Is.EqualTo(expected));
    }

    [Test]
    public void Proposer_boost_reaches_ancestor_payload_nodes_but_not_the_boosted_block_own()
    {
        // 32 validators of 32 Gwei: the committee fraction is 32 * 32 / 32 * 40 / 100 = 12.
        ulong[] balances = Enumerable.Repeat(32ul, 32).ToArray();
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0), isGloas: true, parentBlockHash: GetRoot(0)), 2, Anchor, Anchor);
        fc.ProcessBlock(Block(2, GetRoot(2), GetRoot(1), isGloas: true, parentBlockHash: OtherHash), 2, Anchor, Anchor);
        ulong score = fc.CalculateCommitteeFraction(JustifiedBalances.FromEffectiveBalances(balances), ProtoArrayForkChoice.DefaultProposerScoreBoostPercent);

        Settle(fc, balances, 2, boost: GetRoot(2));
        ProtoNode parent = Node(fc, GetRoot(1));
        ProtoNode boosted = Node(fc, GetRoot(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(score, Is.EqualTo(12ul));
            Assert.That(boosted.Weight, Is.EqualTo(score));
            Assert.That(boosted.EmptyWeight, Is.Zero, "is_ancestor((B, PENDING), (B, EMPTY)) is false");
            Assert.That(boosted.FullWeight, Is.Zero, "is_ancestor((B, PENDING), (B, FULL)) is false");
            Assert.That(parent.EmptyWeight, Is.EqualTo(score), "B builds on its parent empty");
            Assert.That(parent.FullWeight, Is.Zero);
            Assert.That(Node(fc, GetRoot(0)).FullWeight, Is.EqualTo(score));
        }

        Settle(fc, balances, 3);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(boosted.Weight, Is.Zero, "the boost is removed on the next update");
        Assert.That(parent.EmptyWeight, Is.Zero);
        Assert.That(parent.Weight, Is.Zero);
    }

    [Test]
    public void Proposer_boost_never_accumulates_on_a_zero_root_anchor()
    {
        // The zero root aliases the anchor and pass 1 never removes a boost from it, so the boost walk must not add one.
        CheckpointRef zeroAnchor = new(0, Hash256.Zero);
        ulong[] balances = Enumerable.Repeat(32ul, 32).ToArray();
        ProtoArrayForkChoice fc = new(0, 0, Hash256.Zero, zeroAnchor, zeroAnchor, ExecutionStatus.Optimistic, Hash256.Zero, SlotsPerEpoch);
        fc.ProcessBlock(Block(1, GetRoot(1), Hash256.Zero) with { JustifiedCheckpoint = zeroAnchor, FinalizedCheckpoint = zeroAnchor }, 1, zeroAnchor, zeroAnchor);
        JustifiedBalances justifiedBalances = JustifiedBalances.FromEffectiveBalances(balances);

        for (ulong slot = 1; slot <= 4; slot++)
        {
            fc.SetProposerBoostRoot(slot < 4 ? GetRoot(1) : Hash256.Zero);
            fc.GetHead(zeroAnchor, zeroAnchor, justifiedBalances, null, slot);
            Assert.That(Node(fc, GetRoot(1)).Weight, Is.EqualTo(slot < 4 ? 12ul : 0ul), $"fixture: the boost reaches the child at slot {slot}");
        }

        ProtoNode anchor = Node(fc, Hash256.Zero);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(anchor.Weight, Is.Zero);
        Assert.That(anchor.EmptyWeight, Is.Zero);
        Assert.That(anchor.FullWeight, Is.Zero);
    }

    [Test]
    public void Payload_status_change_on_the_same_root_moves_bucket_weight_only()
    {
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0), isGloas: true, parentBlockHash: GetRoot(0)), 4, Anchor, Anchor);

        fc.ProcessAttestation(0, GetRoot(1), 2, 0, payloadPresent: false);
        Settle(fc, [9], 4);
        fc.ProcessAttestation(0, GetRoot(1), 3, 0, payloadPresent: true);
        Settle(fc, [9], 4);

        ProtoNode block = Node(fc, GetRoot(1));
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(block.EmptyWeight, Is.Zero);
        Assert.That(block.FullWeight, Is.EqualTo(9ul));
        Assert.That(block.Weight, Is.EqualTo(9ul));
    }

    [Test]
    public void Equivocating_validator_is_removed_from_the_payload_node_it_supported()
    {
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0), isGloas: true, parentBlockHash: GetRoot(0)), 4, Anchor, Anchor);
        fc.ProcessAttestation(0, GetRoot(1), 2, 0, payloadPresent: true);
        fc.ProcessAttestation(1, GetRoot(1), 2, 0, payloadPresent: true);
        Settle(fc, [5, 6], 4);

        fc.OnAttesterSlashing([0ul]);
        Settle(fc, [5, 6], 4);
        Settle(fc, [5, 6], 4);

        ProtoNode block = Node(fc, GetRoot(1));
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(block.FullWeight, Is.EqualTo(6ul), "deducted exactly once");
        Assert.That(block.Weight, Is.EqualTo(6ul));
    }


    [Test]
    public void Prune_keeps_every_child_index_pointing_at_its_child()
    {
        ProtoArrayForkChoice fc = NewForkChoice();
        fc.PruneThreshold = 0;
        fc.ProcessBlock(Block(1, GetRoot(1), GetRoot(0)), 4, Anchor, Anchor);
        fc.ProcessBlock(Block(2, GetRoot(2), GetRoot(1)), 4, Anchor, Anchor);
        fc.ProcessBlock(Block(2, GetRoot(3), GetRoot(1)), 4, Anchor, Anchor);
        fc.ProcessBlock(Block(3, GetRoot(4), GetRoot(2)), 4, Anchor, Anchor);

        fc.MaybePrune(GetRoot(1));

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fc.Nodes, Has.Count.EqualTo(4));
        Assert.That(Node(fc, GetRoot(1)).Children.Select(i => fc.Nodes[i].Root), Is.EqualTo(new[] { GetRoot(2), GetRoot(3) }));
        Assert.That(Node(fc, GetRoot(2)).Children.Select(i => fc.Nodes[i].Root), Is.EqualTo(new[] { GetRoot(4) }));
        Assert.That(Node(fc, GetRoot(3)).Children, Is.Empty);
    }


}
