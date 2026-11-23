// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.ForkChoice.ProtoArrayTestBlocks;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public class FastConfirmationScoresTests
{
    [Test]
    public void Committing_a_zero_root_epoch_zero_vote_removes_unset_support()
    {
        CheckpointRef anchor = new(0, Hash256.Zero);
        ProtoArrayForkChoice tree = new(4, 0, Hash256.Zero, anchor, anchor, ExecutionStatus.Optimistic, Hash256.Zero);
        tree.ProcessBlock(CreateProtoBlock(1, GetRoot(1), Hash256.Zero, anchor, anchor, ExecutionStatus.Optimistic, GetRoot(1)), 4, anchor, anchor);
        JustifiedBalances balances = JustifiedBalances.FromEffectiveBalances([32]);
        FastConfirmationScores scores = new([32], [false]);
        tree.LatestVoteChanged += scores.MarkDirty;
        HashSet<ulong> equivocators = [];
        tree.ProcessAttestation(0, GetRoot(1), 2, 0, payloadPresent: true);
        tree.GetHead(anchor, anchor, balances, equivocators, 4);
        tree.ProcessAttestation(0, Hash256.Zero, 3, 0, payloadPresent: true);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(scores.Calculate(tree, equivocators), Is.EqualTo(new ulong[] { 32, 0 }));
        tree.GetHead(anchor, anchor, balances, equivocators, 4);
        Assert.That(tree.LatestVote(0), Is.Null);
        Assert.That(scores.Calculate(tree, equivocators), Is.EqualTo(new ulong[] { 0, 0 }));
    }

    [Test]
    public void Incremental_scores_match_full_scan_across_vote_and_tree_changes([Values(19, 71)] int seed)
    {
        Random random = new(seed);
        CheckpointRef anchor = new(0, GetRoot(1));
        ProtoArrayForkChoice tree = new(1000, 0, Hash256.Zero, anchor, anchor, ExecutionStatus.Optimistic, anchor.Root)
        {
            PruneThreshold = 0,
        };
        for (ulong i = 2; i <= 12; i++) Add(i, i / 2);
        HashSet<ulong> equivocators = [];
        ulong[] balances = [];
        bool[] slashed = [];
        FastConfirmationScores scores = null!;
        ReplaceSource(96);

        for (int step = 1; step <= 60; step++)
        {
            for (int move = 0; move < 5; move++)
            {
                ulong validator = (ulong)random.Next(128);
                tree.ProcessAttestation(validator, GetRoot((ulong)random.Next(2, 17)), (ulong)step * 32, (ulong)step,
                    payloadPresent: (move & 1) == 0);
            }
            ulong equivocator = (ulong)random.Next(96);
            if (!equivocators.Add(equivocator)) equivocators.Remove(equivocator);
            if (step % 7 == 0) equivocators.Clear();
            if (step == 10) { Add(13, 6); Add(14, 6); }
            if (step == 20) { Add(15, 7); Add(16, 7); }
            if (step == 25)
                tree.GetHead(anchor, anchor, JustifiedBalances.FromEffectiveBalances(balances), equivocators, 1000);
            if (step == 30) tree.MaybePrune(GetRoot(6));
            if (step == 40) ReplaceSource(64);
            if (step == 50) ReplaceSource(128);

            ulong[] expected = ReferenceScores(tree, balances, slashed, equivocators);
            using IDisposable scope = Assert.EnterMultipleScope();
            Assert.That(scores.Calculate(tree, equivocators), Is.EqualTo(expected), $"step {step}");
            Assert.That(scores.Calculate(tree, equivocators), Is.EqualTo(expected), $"unchanged step {step}");
        }

        void Add(ulong root, ulong parent) => tree.ProcessBlock(CreateProtoBlock(root, GetRoot(root), GetRoot(parent),
            anchor, anchor, ExecutionStatus.Optimistic, GetRoot(root)), 1000, anchor, anchor);

        void ReplaceSource(int count)
        {
            if (scores is not null) tree.LatestVoteChanged -= scores.MarkDirty;
            balances = new ulong[count];
            slashed = new bool[count];
            for (int i = 0; i < count; i++)
            {
                balances[i] = (ulong)random.Next(0, 33);
                slashed[i] = random.Next(5) == 0;
            }
            scores = new FastConfirmationScores(balances, slashed);
            tree.LatestVoteChanged += scores.MarkDirty;
        }
    }

    [Test]
    public void Unknown_votes_become_counted_on_insertion_and_status_changes_preserve_scores()
    {
        CheckpointRef anchor = new(0, GetRoot(1));
        ProtoArrayForkChoice tree = new(64, 0, Hash256.Zero, anchor, anchor, ExecutionStatus.Optimistic, anchor.Root);
        FastConfirmationScores scores = new([32], [false]);
        tree.LatestVoteChanged += scores.MarkDirty;
        HashSet<ulong> equivocators = [];
        using IDisposable scope = Assert.EnterMultipleScope();
        tree.ProcessAttestation(0, GetRoot(2), 1);
        Assert.That(scores.Calculate(tree, equivocators), Is.EqualTo(new ulong[] { 0 }));
        tree.ProcessBlock(CreateProtoBlock(1, GetRoot(2), anchor.Root, anchor, anchor, ExecutionStatus.Optimistic, GetRoot(2)), 64, anchor, anchor);
        Assert.That(scores.Calculate(tree, equivocators), Is.EqualTo(new ulong[] { 32, 32 }));

        tree.ProcessAttestation(0, GetRoot(2), 33, 1, payloadPresent: false);
        Assert.That(tree.LatestVote(0)!.Value.NextStatus, Is.EqualTo(ForkChoicePayloadStatus.Empty));
        Assert.That(scores.Calculate(tree, equivocators), Is.EqualTo(new ulong[] { 32, 32 }));
        tree.ProcessAttestation(0, GetRoot(2), 34, 1, payloadPresent: true);
        Assert.That(tree.LatestVote(0)!.Value.NextStatus, Is.EqualTo(ForkChoicePayloadStatus.Full));
        Assert.That(scores.Calculate(tree, equivocators), Is.EqualTo(new ulong[] { 32, 32 }));
        equivocators.Add(0);
        Assert.That(scores.Calculate(tree, equivocators), Is.EqualTo(new ulong[] { 0, 0 }));
        equivocators.Clear();
        Assert.That(scores.Calculate(tree, equivocators), Is.EqualTo(new ulong[] { 32, 32 }));
    }

    private static ulong[] ReferenceScores(ProtoArrayForkChoice tree, ulong[] balances, bool[] slashed, IReadOnlySet<ulong> equivocators)
    {
        ulong[] scores = new ulong[tree.Nodes.Count];
        for (int i = 0; i < balances.Length; i++)
        {
            if (!slashed[i] && balances[i] != 0 && !equivocators.Contains((ulong)i)
                && tree.LatestVote((ulong)i) is VoteTracker vote && tree.IndexOf(vote.NextRoot) is int index)
                scores[index] += balances[i];
        }
        for (int i = scores.Length - 1; i >= 0; i--)
            if (tree.Nodes[i].Parent is int parent) scores[parent] += scores[i];
        return scores;
    }
}
