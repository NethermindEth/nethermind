// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.BeaconChain.ForkChoice;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>Port of Lighthouse's <c>fork_choice_test_definition/votes.rs</c>.</summary>
public static class VotesTestDefinition
{
    public static ForkChoiceTestDefinition Get()
    {
        ulong[] twoValidators = [1, 1];
        ulong[] fourValidators = [1, 1, 1, 1];
        ulong[] lastTwoExited = [1, 1, 0, 0];
        CheckpointRef anchor = new(1, GetRoot(0));
        CheckpointRef justified5 = new(2, GetRoot(5));

        List<Operation> operations =
        [
            // Ensure that the head starts at the finalized block.
            new FindHead(anchor, anchor, twoValidators, GetRoot(0)),
            // Add block 2:  0 <- 2
            new ProcessBlock(1, GetRoot(2), GetRoot(0), anchor, anchor),
            // Ensure that the head is 2.
            new FindHead(anchor, anchor, twoValidators, GetRoot(2)),
            // Add block 1 forking from 0:  0 <- (2 | 1)
            new ProcessBlock(1, GetRoot(1), GetRoot(0), anchor, anchor),
            // Ensure that the head is still 2.
            new FindHead(anchor, anchor, twoValidators, GetRoot(2)),
            // Add a vote to block 1.
            new ProcessAttestation(0, GetRoot(1), 2),
            // Ensure that the head is now 1, because 1 has a vote.
            new FindHead(anchor, anchor, twoValidators, GetRoot(1)),
            // Add a vote to block 2.
            new ProcessAttestation(1, GetRoot(2), 2),
            // Ensure that the head is 2 since 1 and 2 both have a vote (2 wins the tie-break).
            new FindHead(anchor, anchor, twoValidators, GetRoot(2)),
            // Add block 3 on 1:  0 <- (2 | 1 <- 3)
            new ProcessBlock(2, GetRoot(3), GetRoot(1), anchor, anchor),
            // Ensure that the head is still 2.
            new FindHead(anchor, anchor, twoValidators, GetRoot(2)),
            // Move validator #0's vote from 1 to 3.
            new ProcessAttestation(0, GetRoot(3), 3),
            // Ensure that the head is still 2.
            new FindHead(anchor, anchor, twoValidators, GetRoot(2)),
            // Move validator #1's vote from 2 to 1 (an equivocation, but fork choice doesn't care).
            new ProcessAttestation(1, GetRoot(1), 3),
            // Ensure that the head is now 3.
            new FindHead(anchor, anchor, twoValidators, GetRoot(3)),
            // Add block 4 on 3.
            new ProcessBlock(3, GetRoot(4), GetRoot(3), anchor, anchor),
            // Ensure that the head is now 4.
            new FindHead(anchor, anchor, twoValidators, GetRoot(4)),
            // Add block 5 on 4, which has a justified epoch of 2.
            new ProcessBlock(4, GetRoot(5), GetRoot(4), new(2, GetRoot(1)), new(2, GetRoot(1))),
            // Ensure that 5 is filtered out and the head stays at 4.
            new FindHead(anchor, anchor, twoValidators, GetRoot(4)),
            // Add block 6 on 4, which has a justified epoch of 1.
            new ProcessBlock(0, GetRoot(6), GetRoot(4), anchor, anchor),
            // Move both votes to 5.
            new ProcessAttestation(0, GetRoot(5), 4),
            new ProcessAttestation(1, GetRoot(5), 4),
            // Add blocks 7, 8 and 9 on 5, exercising the best_descendant functionality.
            new ProcessBlock(0, GetRoot(7), GetRoot(5), justified5, justified5),
            new ProcessBlock(0, GetRoot(8), GetRoot(7), justified5, justified5),
            new ProcessBlock(0, GetRoot(9), GetRoot(8), justified5, justified5),
            // Ensure that 6 is the head: even though 5 has all the votes it is filtered out due to
            // a differing justified epoch.
            new FindHead(anchor, anchor, twoValidators, GetRoot(6)),
            // Change the fork-choice justified checkpoint to (2, 5) and ensure that 9 is the head.
            new FindHead(justified5, justified5, twoValidators, GetRoot(9)),
            // Move both votes to 9.
            new ProcessAttestation(0, GetRoot(9), 5),
            new ProcessAttestation(1, GetRoot(9), 5),
            // Add block 10 on 8.
            new ProcessBlock(0, GetRoot(10), GetRoot(8), justified5, justified5),
            // Double-check the head is still 9.
            new FindHead(justified5, justified5, twoValidators, GetRoot(9)),
            // Introduce 2 more validators into the system and have them vote for 10.
            new ProcessAttestation(2, GetRoot(10), 5),
            new ProcessAttestation(3, GetRoot(10), 5),
            // Check the head is now 10.
            new FindHead(justified5, justified5, fourValidators, GetRoot(10)),
            // Set the balances of the last two validators to zero; the head is 9 again.
            new FindHead(justified5, justified5, lastTwoExited, GetRoot(9)),
            // Set the balances of the last two validators back to 1; the head is 10.
            new FindHead(justified5, justified5, fourValidators, GetRoot(10)),
            // Remove the last two validators; the head is 9 again.
            new FindHead(justified5, justified5, twoValidators, GetRoot(9)),
            // Ensure that pruning below the prune threshold does not prune.
            new Prune(GetRoot(5), int.MaxValue, 11),
            // Run find-head, ensure the no-op prune didn't change the head.
            new FindHead(justified5, justified5, twoValidators, GetRoot(9)),
            // Ensure that pruning above the prune threshold does prune (0, 1, 2, 3 and 4 dropped).
            new Prune(GetRoot(5), 1, 6),
            // Run find-head, ensure the prune didn't change the head.
            new FindHead(justified5, justified5, twoValidators, GetRoot(9)),
            // Add block 11 on 9.
            new ProcessBlock(0, GetRoot(11), GetRoot(9), justified5, justified5),
            // Ensure the head is now 11.
            new FindHead(justified5, justified5, twoValidators, GetRoot(11)),
        ];

        return new ForkChoiceTestDefinition
        {
            FinalizedBlockSlot = 0,
            JustifiedCheckpoint = anchor,
            FinalizedCheckpoint = anchor,
            Operations = operations,
        };
    }
}
