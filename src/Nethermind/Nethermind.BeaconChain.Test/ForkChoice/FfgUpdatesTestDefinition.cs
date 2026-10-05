// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public static class FfgUpdatesTestDefinition
{
    public static ForkChoiceTestDefinition GetCase01()
    {
        ulong[] balances = [1, 1];

        List<Operation> operations =
        [
            new FindHead(GetCheckpoint(0), GetCheckpoint(0), balances, GetRoot(0)),
            new ProcessBlock(1, GetRoot(1), GetRoot(0), GetCheckpoint(0), GetCheckpoint(0)),
            new ProcessBlock(2, GetRoot(2), GetRoot(1), GetCheckpoint(1), GetCheckpoint(0)),
            new ProcessBlock(3, GetRoot(3), GetRoot(2), GetCheckpoint(2), GetCheckpoint(1)),
            new FindHead(GetCheckpoint(0), GetCheckpoint(0), balances, GetRoot(3)),
            new FindHead(GetCheckpoint(1), GetCheckpoint(0), balances, GetRoot(3)),
            new FindHead(GetCheckpoint(2), GetCheckpoint(1), balances, GetRoot(3)),
        ];

        return ForkChoiceTestDefinition.Create(GetCheckpoint(0), operations);
    }

    public static ForkChoiceTestDefinition GetCase02()
    {
        ulong[] balances = [1, 1];

        List<Operation> operations =
        [
            new FindHead(GetCheckpoint(0), GetCheckpoint(0), balances, GetRoot(0)),
            new ProcessBlock(1, GetRoot(1), GetRoot(0), GetCheckpoint(0), GetCheckpoint(0)),
            new ProcessBlock(2, GetRoot(3), GetRoot(1), new(1, GetRoot(1)), GetCheckpoint(0)),
            new ProcessBlock(3, GetRoot(5), GetRoot(3), new(1, GetRoot(1)), GetCheckpoint(0)),
            new ProcessBlock(4, GetRoot(7), GetRoot(5), new(1, GetRoot(1)), GetCheckpoint(0)),
            new ProcessBlock(5, GetRoot(9), GetRoot(7), new(2, GetRoot(3)), GetCheckpoint(0)),
            new ProcessBlock(1, GetRoot(2), GetRoot(0), GetCheckpoint(0), GetCheckpoint(0)),
            new ProcessBlock(2, GetRoot(4), GetRoot(2), GetCheckpoint(0), GetCheckpoint(0)),
            new ProcessBlock(3, GetRoot(6), GetRoot(4), GetCheckpoint(0), GetCheckpoint(0)),
            new ProcessBlock(4, GetRoot(8), GetRoot(6), new(1, GetRoot(2)), GetCheckpoint(0)),
            new ProcessBlock(5, GetRoot(10), GetRoot(8), new(2, GetRoot(4)), GetCheckpoint(0)),
            new FindHead(GetCheckpoint(0), GetCheckpoint(0), balances, GetRoot(10)),
            new FindHead(new(2, GetRoot(4)), GetCheckpoint(0), balances, GetRoot(10)),
            new FindHead(new(3, GetRoot(6)), GetCheckpoint(0), balances, GetRoot(10)),
            new ProcessAttestation(0, GetRoot(1), 0),
            new FindHead(GetCheckpoint(0), GetCheckpoint(0), balances, GetRoot(9)),
            new FindHead(new(2, GetRoot(3)), GetCheckpoint(0), balances, GetRoot(9)),
            new FindHead(new(3, GetRoot(5)), GetCheckpoint(0), balances, GetRoot(9)),
            new ProcessAttestation(1, GetRoot(2), 0),
            new FindHead(GetCheckpoint(0), GetCheckpoint(0), balances, GetRoot(10)),
            new FindHead(new(2, GetRoot(4)), GetCheckpoint(0), balances, GetRoot(10)),
            new FindHead(new(3, GetRoot(6)), GetCheckpoint(0), balances, GetRoot(10)),
            new FindHead(new(0, GetRoot(1)), GetCheckpoint(0), balances, GetRoot(9)),
            new FindHead(new(2, GetRoot(3)), GetCheckpoint(0), balances, GetRoot(9)),
            new FindHead(new(3, GetRoot(5)), GetCheckpoint(0), balances, GetRoot(9)),
            new FindHead(GetCheckpoint(0), GetCheckpoint(0), balances, GetRoot(10)),
            new FindHead(new(2, GetRoot(4)), GetCheckpoint(0), balances, GetRoot(10)),
            new FindHead(new(3, GetRoot(6)), GetCheckpoint(0), balances, GetRoot(10)),
        ];

        return ForkChoiceTestDefinition.Create(GetCheckpoint(0), operations);
    }
}
