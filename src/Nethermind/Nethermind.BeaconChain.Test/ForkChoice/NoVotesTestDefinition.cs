// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>Port of Lighthouse's <c>fork_choice_test_definition/no_votes.rs</c>.</summary>
public static class NoVotesTestDefinition
{
    public static ForkChoiceTestDefinition Get()
    {
        ulong[] balances = new ulong[16];
        CheckpointRef genesis = new(1, Hash256.Zero);

        List<Operation> operations =
        [
            // Check that the head is the finalized block.
            new FindHead(genesis, genesis, balances, Hash256.Zero),
            // Add block 2:  0 <- 2
            new ProcessBlock(1, GetRoot(2), Hash256.Zero, genesis, genesis),
            // Ensure the head is 2.
            new FindHead(genesis, genesis, balances, GetRoot(2)),
            // Add block 1 forking from 0:  0 <- (2 | 1)
            new ProcessBlock(1, GetRoot(1), GetRoot(0), genesis, genesis),
            // Ensure the head is still 2.
            new FindHead(genesis, genesis, balances, GetRoot(2)),
            // Add block 3 on 1:  0 <- (2 | 1 <- 3)
            new ProcessBlock(2, GetRoot(3), GetRoot(1), genesis, genesis),
            // Ensure 2 is still the head.
            new FindHead(genesis, genesis, balances, GetRoot(2)),
            // Add block 4 on 2:  0 <- (2 <- 4 | 1 <- 3)
            new ProcessBlock(2, GetRoot(4), GetRoot(2), genesis, genesis),
            // Ensure the head is 4.
            new FindHead(genesis, genesis, balances, GetRoot(4)),
            // Add block 5 on 4 with a justified epoch of 2.
            new ProcessBlock(3, GetRoot(5), GetRoot(4), GetCheckpoint(2), genesis),
            // Ensure the head is now 5 whilst the store's justified epoch is 1.
            new FindHead(genesis, genesis, balances, GetRoot(5)),
            // Starting from 5 with a lower justified epoch is allowed since
            // https://github.com/ethereum/consensus-specs/pull/3431.
            new FindHead(new(1, GetRoot(5)), genesis, balances, GetRoot(5)),
            // Set the justified epoch to 2 and the start block to 5 and ensure 5 is the head.
            new FindHead(GetCheckpoint(2), genesis, balances, GetRoot(5)),
            // Add block 6 on 5.
            new ProcessBlock(4, GetRoot(6), GetRoot(5), GetCheckpoint(2), genesis),
            // Ensure 6 is the head.
            new FindHead(GetCheckpoint(2), genesis, balances, GetRoot(6)),
        ];

        return new ForkChoiceTestDefinition
        {
            FinalizedBlockSlot = 0,
            JustifiedCheckpoint = genesis,
            FinalizedCheckpoint = genesis,
            Operations = operations,
        };
    }
}
