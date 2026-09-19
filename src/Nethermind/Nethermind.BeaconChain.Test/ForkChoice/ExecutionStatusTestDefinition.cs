// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BeaconChain.ForkChoice;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>Port of Lighthouse's <c>fork_choice_test_definition/execution_status.rs</c> (all three scenarios).</summary>
public static class ExecutionStatusTestDefinition
{
    private static readonly CheckpointRef Anchor = new(1, GetRoot(0));

    private static ForkChoiceTestDefinition Definition(List<Operation> operations) => new()
    {
        FinalizedBlockSlot = 0,
        JustifiedCheckpoint = Anchor,
        FinalizedCheckpoint = Anchor,
        Operations = operations,
    };

    private static FindHead Head(ulong[] balances, ulong expectedHead) => new(Anchor, Anchor, balances, GetRoot(expectedHead));

    /// <summary>
    /// The opening shared by scenarios 01 and 02: build 0 &lt;- (2 | 1 &lt;- 3) with a vote on each
    /// fork, then move validator #0's vote from 1 to 3.
    /// </summary>
    private static List<Operation> CommonPrologue(ulong[] balances) =>
    [
        // Ensure that the head starts at the finalized block.
        Head(balances, 0),
        // Add block 2:  0 <- 2
        new ProcessBlock(1, GetRoot(2), GetRoot(0), Anchor, Anchor),
        // Ensure that the head is 2.
        Head(balances, 2),
        // Add block 1 forking from 0:  0 <- (2 | 1)
        new ProcessBlock(1, GetRoot(1), GetRoot(0), Anchor, Anchor),
        // Ensure that the head is still 2.
        Head(balances, 2),
        // Add a vote to block 1; it becomes the head.
        new ProcessAttestation(0, GetRoot(1), 2),
        Head(balances, 1),
        new AssertWeight(GetRoot(0), 1),
        new AssertWeight(GetRoot(1), 1),
        new AssertWeight(GetRoot(2), 0),
        // Add a vote to block 2; it becomes the head again (tie-break).
        new ProcessAttestation(1, GetRoot(2), 2),
        Head(balances, 2),
        new AssertWeight(GetRoot(0), 2),
        new AssertWeight(GetRoot(1), 1),
        new AssertWeight(GetRoot(2), 1),
        // Add block 3 on 1:  0 <- (2 | 1 <- 3)
        new ProcessBlock(2, GetRoot(3), GetRoot(1), Anchor, Anchor),
        // Ensure that the head is still 2.
        Head(balances, 2),
        new AssertWeight(GetRoot(0), 2),
        new AssertWeight(GetRoot(1), 1),
        new AssertWeight(GetRoot(2), 1),
        new AssertWeight(GetRoot(3), 0),
        // Move validator #0's vote from 1 to 3.
        new ProcessAttestation(0, GetRoot(3), 3),
    ];

    public static ForkChoiceTestDefinition Get01()
    {
        ulong[] balances = [1, 1];

        List<Operation> operations = CommonPrologue(balances);
        operations.AddRange(
        [
            // Ensure that the head is still 2.
            Head(balances, 2),
            new AssertWeight(GetRoot(0), 2),
            new AssertWeight(GetRoot(1), 1),
            new AssertWeight(GetRoot(2), 1),
            new AssertWeight(GetRoot(3), 1),
            // Invalidate the payload of 3, with 1 as the latest valid ancestor.
            new InvalidatePayload(GetRoot(3), GetRoot(1)),
            // Ensure that the head is still 2.
            Head(balances, 2),
            // Invalidation of 3 should have removed its weight upstream.
            new AssertWeight(GetRoot(0), 1),
            new AssertWeight(GetRoot(1), 0),
            new AssertWeight(GetRoot(2), 1),
            new AssertWeight(GetRoot(3), 0),
            // Move a vote from 2 to 1 (slashable, but irrelevant here); the head switches back to 1.
            new ProcessAttestation(1, GetRoot(1), 3),
            Head(balances, 1),
            new AssertWeight(GetRoot(0), 1),
            new AssertWeight(GetRoot(1), 1),
            new AssertWeight(GetRoot(2), 0),
            new AssertWeight(GetRoot(3), 0),
        ]);

        return Definition(operations);
    }

    public static ForkChoiceTestDefinition Get02()
    {
        ulong[] balances = [1, 1];

        List<Operation> operations = CommonPrologue(balances);
        operations.AddRange(
        [
            // Move validator #1's vote from 2 to 3 as well; the head becomes 3.
            new ProcessAttestation(1, GetRoot(3), 3),
            Head(balances, 3),
            new AssertWeight(GetRoot(0), 2),
            new AssertWeight(GetRoot(1), 2),
            new AssertWeight(GetRoot(2), 0),
            new AssertWeight(GetRoot(3), 2),
            // Invalidate the payload of 3, with 1 as the latest valid ancestor.
            new InvalidatePayload(GetRoot(3), GetRoot(1)),
            // Ensure that the head is now 2.
            Head(balances, 2),
            // Invalidation of 3 should have removed all weight (both votes were on its chain).
            new AssertWeight(GetRoot(0), 0),
            new AssertWeight(GetRoot(1), 0),
            new AssertWeight(GetRoot(2), 0),
            new AssertWeight(GetRoot(3), 0),
        ]);

        return Definition(operations);
    }

    public static ForkChoiceTestDefinition Get03()
    {
        ulong[] balances = new ulong[2_000];
        Array.Fill(balances, 1_000UL);

        List<Operation> operations =
        [
            // Ensure that the head starts at the finalized block.
            Head(balances, 0),
            // Add block 2:  0 <- 2
            new ProcessBlock(1, GetRoot(2), GetRoot(0), Anchor, Anchor),
            // Ensure that the head is 2.
            Head(balances, 2),
            // Add block 1 forking from 0:  0 <- (2 | 1)
            new ProcessBlock(1, GetRoot(1), GetRoot(0), Anchor, Anchor),
            // Ensure that the head is still 2.
            Head(balances, 2),
            // Add a vote to block 1; it becomes the head.
            new ProcessAttestation(0, GetRoot(1), 2),
            Head(balances, 1),
            new AssertWeight(GetRoot(0), 1_000),
            new AssertWeight(GetRoot(1), 1_000),
            new AssertWeight(GetRoot(2), 0),
            // Add another vote to 1.
            new ProcessAttestation(1, GetRoot(1), 2),
            Head(balances, 1),
            new AssertWeight(GetRoot(0), 2_000),
            new AssertWeight(GetRoot(1), 2_000),
            new AssertWeight(GetRoot(2), 0),
            // Add block 3 on 1:  0 <- (2 | 1 <- 3)
            new ProcessBlock(2, GetRoot(3), GetRoot(1), Anchor, Anchor),
            // Ensure that the head is now 3, applying a proposer boost to 3 as well.
            new FindHead(Anchor, Anchor, balances, GetRoot(3), ProposerBoostRoot: GetRoot(3)),
            new AssertWeight(GetRoot(0), 33_250),
            new AssertWeight(GetRoot(1), 33_250),
            new AssertWeight(GetRoot(2), 0),
            // A "magic number" from calculate_committee_fraction: 2_000_000 / 32 * 50%.
            new AssertWeight(GetRoot(3), 31_250),
            // Invalidate the payload of 3, with 1 as the latest valid ancestor.
            new InvalidatePayload(GetRoot(3), GetRoot(1)),
            // Ensure that the head is now 1, maintaining the proposer boost root on the invalid block.
            new FindHead(Anchor, Anchor, balances, GetRoot(1), ProposerBoostRoot: GetRoot(3)),
            new AssertWeight(GetRoot(0), 2_000),
            new AssertWeight(GetRoot(1), 2_000),
            new AssertWeight(GetRoot(2), 0),
            // The proposer boost should be reverted due to the invalid payload.
            new AssertWeight(GetRoot(3), 0),
        ];

        return Definition(operations);
    }
}
