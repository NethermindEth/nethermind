// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.BeaconChain.ForkChoice;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>Port of Lighthouse's <c>fork_choice_test_definition/ffg_updates.rs</c>.</summary>
public static class FfgUpdatesTestDefinition
{
    public static ForkChoiceTestDefinition GetCase01()
    {
        ulong[] balances = [1, 1];

        List<Operation> operations =
        [
            // Ensure that the head starts at the finalized block.
            new FindHead(GetCheckpoint(0), GetCheckpoint(0), balances, GetRoot(0)),
            // Build a chain: 0 (just: 0, fin: 0) <- 1 (0, 0) <- 2 (1, 0) <- 3 (2, 1).
            new ProcessBlock(1, GetRoot(1), GetRoot(0), GetCheckpoint(0), GetCheckpoint(0)),
            new ProcessBlock(2, GetRoot(2), GetRoot(1), GetCheckpoint(1), GetCheckpoint(0)),
            new ProcessBlock(3, GetRoot(3), GetRoot(2), GetCheckpoint(2), GetCheckpoint(1)),
            // Ensure that with justified epoch 0 we find 3.
            new FindHead(GetCheckpoint(0), GetCheckpoint(0), balances, GetRoot(3)),
            // Ensure that with justified epoch 1 we find 3: electing a head with a higher justified
            // checkpoint than the store is valid since
            // https://github.com/ethereum/consensus-specs/pull/3431.
            new FindHead(GetCheckpoint(1), GetCheckpoint(0), balances, GetRoot(3)),
            // Ensure that with justified epoch 2 we find 3.
            new FindHead(GetCheckpoint(2), GetCheckpoint(1), balances, GetRoot(3)),
        ];

        return new ForkChoiceTestDefinition
        {
            FinalizedBlockSlot = 0,
            JustifiedCheckpoint = GetCheckpoint(0),
            FinalizedCheckpoint = GetCheckpoint(0),
            Operations = operations,
        };
    }

    public static ForkChoiceTestDefinition GetCase02()
    {
        ulong[] balances = [1, 1];

        // Build the following tree:
        //
        //                       0
        //                      / \
        //  just: 0, fin: 0 -> 1   2 <- just: 0, fin: 0
        //  just: 1, fin: 0 -> 3   4 <- just: 0, fin: 0
        //  just: 1, fin: 0 -> 5   6 <- just: 0, fin: 0
        //  just: 1, fin: 0 -> 7   8 <- just: 1, fin: 0
        //  just: 2, fin: 0 -> 9  10 <- just: 2, fin: 0
        List<Operation> operations =
        [
            // Ensure that the head starts at the finalized block.
            new FindHead(GetCheckpoint(0), GetCheckpoint(0), balances, GetRoot(0)),
            // Left branch.
            new ProcessBlock(1, GetRoot(1), GetRoot(0), GetCheckpoint(0), GetCheckpoint(0)),
            new ProcessBlock(2, GetRoot(3), GetRoot(1), new(1, GetRoot(1)), GetCheckpoint(0)),
            new ProcessBlock(3, GetRoot(5), GetRoot(3), new(1, GetRoot(1)), GetCheckpoint(0)),
            new ProcessBlock(4, GetRoot(7), GetRoot(5), new(1, GetRoot(1)), GetCheckpoint(0)),
            new ProcessBlock(5, GetRoot(9), GetRoot(7), new(2, GetRoot(3)), GetCheckpoint(0)),
            // Right branch.
            new ProcessBlock(1, GetRoot(2), GetRoot(0), GetCheckpoint(0), GetCheckpoint(0)),
            new ProcessBlock(2, GetRoot(4), GetRoot(2), GetCheckpoint(0), GetCheckpoint(0)),
            new ProcessBlock(3, GetRoot(6), GetRoot(4), GetCheckpoint(0), GetCheckpoint(0)),
            new ProcessBlock(4, GetRoot(8), GetRoot(6), new(1, GetRoot(2)), GetCheckpoint(0)),
            new ProcessBlock(5, GetRoot(10), GetRoot(8), new(2, GetRoot(4)), GetCheckpoint(0)),
            // Ensure that if we start at 0 we find 10 (the highest-root tip with no votes); the
            // same head is found with higher store justified epochs since consensus-specs#3431.
            new FindHead(GetCheckpoint(0), GetCheckpoint(0), balances, GetRoot(10)),
            new FindHead(new(2, GetRoot(4)), GetCheckpoint(0), balances, GetRoot(10)),
            new FindHead(new(3, GetRoot(6)), GetCheckpoint(0), balances, GetRoot(10)),
            // Add a vote to 1; the head becomes 9.
            new ProcessAttestation(0, GetRoot(1), 0),
            new FindHead(GetCheckpoint(0), GetCheckpoint(0), balances, GetRoot(9)),
            new FindHead(new(2, GetRoot(3)), GetCheckpoint(0), balances, GetRoot(9)),
            new FindHead(new(3, GetRoot(5)), GetCheckpoint(0), balances, GetRoot(9)),
            // Add a vote to 2; the head becomes 10 again.
            new ProcessAttestation(1, GetRoot(2), 0),
            new FindHead(GetCheckpoint(0), GetCheckpoint(0), balances, GetRoot(10)),
            new FindHead(new(2, GetRoot(4)), GetCheckpoint(0), balances, GetRoot(10)),
            new FindHead(new(3, GetRoot(6)), GetCheckpoint(0), balances, GetRoot(10)),
            // Ensure that if we start at 1 we find 9.
            new FindHead(new(0, GetRoot(1)), GetCheckpoint(0), balances, GetRoot(9)),
            new FindHead(new(2, GetRoot(3)), GetCheckpoint(0), balances, GetRoot(9)),
            new FindHead(new(3, GetRoot(5)), GetCheckpoint(0), balances, GetRoot(9)),
            // Ensure that if we start at 0 (or 2) we find 10.
            new FindHead(GetCheckpoint(0), GetCheckpoint(0), balances, GetRoot(10)),
            new FindHead(new(2, GetRoot(4)), GetCheckpoint(0), balances, GetRoot(10)),
            new FindHead(new(3, GetRoot(6)), GetCheckpoint(0), balances, GetRoot(10)),
        ];

        return new ForkChoiceTestDefinition
        {
            FinalizedBlockSlot = 0,
            JustifiedCheckpoint = GetCheckpoint(0),
            FinalizedCheckpoint = GetCheckpoint(0),
            Operations = operations,
        };
    }
}
