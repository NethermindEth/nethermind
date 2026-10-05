// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.Core.Crypto;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public static class NoVotesTestDefinition
{
    public static ForkChoiceTestDefinition Get()
    {
        ulong[] balances = new ulong[16];
        CheckpointRef genesis = new(1, Hash256.Zero);

        List<Operation> operations =
        [
            new FindHead(genesis, genesis, balances, Hash256.Zero),
            new ProcessBlock(1, GetRoot(2), Hash256.Zero, genesis, genesis),
            new FindHead(genesis, genesis, balances, GetRoot(2)),
            new ProcessBlock(1, GetRoot(1), GetRoot(0), genesis, genesis),
            new FindHead(genesis, genesis, balances, GetRoot(2)),
            new ProcessBlock(2, GetRoot(3), GetRoot(1), genesis, genesis),
            new FindHead(genesis, genesis, balances, GetRoot(2)),
            new ProcessBlock(2, GetRoot(4), GetRoot(2), genesis, genesis),
            new FindHead(genesis, genesis, balances, GetRoot(4)),
            new ProcessBlock(3, GetRoot(5), GetRoot(4), GetCheckpoint(2), genesis),
            new FindHead(genesis, genesis, balances, GetRoot(5)),
            new FindHead(new(1, GetRoot(5)), genesis, balances, GetRoot(5)),
            new FindHead(GetCheckpoint(2), genesis, balances, GetRoot(5)),
            new ProcessBlock(4, GetRoot(6), GetRoot(5), GetCheckpoint(2), genesis),
            new FindHead(GetCheckpoint(2), genesis, balances, GetRoot(6)),
        ];

        return ForkChoiceTestDefinition.Create(genesis, operations);
    }
}
