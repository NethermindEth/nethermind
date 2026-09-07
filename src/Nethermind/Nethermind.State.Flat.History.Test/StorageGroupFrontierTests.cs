// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.State.Flat.History.Walk;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class StorageGroupFrontierTests
{
    private static HistoryWalkMismatch Found(ulong block) => new(block, HistoryWalkMismatchKind.StorageRoot, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256);

    [Test]
    public void Groups_completing_out_of_order_are_merged_in_sequence_and_checkpointed_at_the_contiguous_prefix()
    {
        MismatchSink found = new();
        List<uint> checkpoints = [];
        StorageGroupFrontier frontier = new(found, checkpointGroups: 1, checkpoints.Add);
        MismatchSink[] sinks = [new(), new(), new()];
        long[] sequences = [frontier.Issue(10, sinks[0]), frontier.Issue(20, sinks[1]), frontier.Issue(30, sinks[2])];
        for (int i = 0; i < sinks.Length; i++) sinks[i].Add(Found((ulong)(100 + i)));

        frontier.Complete(sequences[2]);
        List<uint> afterTheLastGroup = [.. checkpoints];
        List<ulong> foundAfterTheLastGroup = found.Drain().Select(static m => m.Block).ToList();
        frontier.Complete(sequences[0]);
        List<uint> afterTheFirstGroup = [.. checkpoints];
        frontier.Complete(sequences[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterTheLastGroup, Is.Empty, "a finished group above an unfinished one is not a checkpoint: a resume would skip the unfinished group");
            Assert.That(foundAfterTheLastGroup, Is.Empty, "its findings wait too, or the checkpoint below would carry findings the resume finds again");
            Assert.That(afterTheFirstGroup, Is.EqualTo(new[] { 10u }));
            Assert.That(checkpoints, Is.EqualTo(new[] { 10u, 20u, 30u }), "once the gap closes, every contiguous group checkpoints in order");
            Assert.That(found.Drain().Select(static m => m.Block), Is.EqualTo(new ulong[] { 100, 101, 102 }));
        }
    }

    [Test]
    public void Group_sinks_waiting_behind_the_frontier_share_one_budget_so_their_aggregate_never_exceeds_the_item_cap()
    {
        MismatchBudget budget = new(3);
        MismatchSink[] sinks = [new(capacity: 10, budget), new(capacity: 10, budget), new(capacity: 10, budget)];
        sinks[2].Add(Found(300));
        sinks[0].AddRange([Found(100), Found(101), Found(102), Found(103)]);
        sinks[1].Add(Found(200));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sinks[2].Count, Is.EqualTo(1));
            Assert.That(sinks[0].Count, Is.EqualTo(2), "the budget grants what is left, not what was asked for");
            Assert.That(sinks[1].Count, Is.Zero, "a group issued after the budget is spent keeps nothing; the item cap would have dropped it anyway");
            Assert.That(sinks.Sum(static sink => sink.Count), Is.EqualTo(3));
        }
    }

    [Test]
    public void Merging_child_sinks_into_their_parent_moves_the_records_without_charging_the_shared_budget_again()
    {
        MismatchBudget budget = new(4);
        MismatchSink parent = new(capacity: 10, budget);
        MismatchSink[] children = [new(capacity: 10, budget), new(capacity: 10, budget)];
        children[0].AddRange([Found(1), Found(2)]);
        children[1].AddRange([Found(3), Found(4)]);

        foreach (MismatchSink child in children) parent.AddRange(child);

        Assert.That(parent.Drain().Select(static m => m.Block), Is.EqualTo(new ulong[] { 1, 2, 3, 4 }), "a merge is a transfer of records that already paid; charging the budget a second time per split level would drop findings the capacity keeps");
    }

    [Test]
    public void A_checkpoint_fires_once_per_batch_of_contiguous_groups()
    {
        List<uint> checkpoints = [];
        StorageGroupFrontier frontier = new(new MismatchSink(), checkpointGroups: 2, checkpoints.Add);
        long first = frontier.Issue(1, new MismatchSink());
        long second = frontier.Issue(2, new MismatchSink());
        long third = frontier.Issue(3, new MismatchSink());

        frontier.Complete(second);
        frontier.Complete(first);
        frontier.Complete(third);

        Assert.That(checkpoints, Is.EqualTo(new[] { 2u }), "two groups per checkpoint: the pair fires at the second prefix, the third waits for a fourth");
    }
}
