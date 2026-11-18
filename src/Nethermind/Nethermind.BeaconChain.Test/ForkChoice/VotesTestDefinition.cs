// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

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
            new FindHead(anchor, anchor, twoValidators, GetRoot(0)),
            new ProcessBlock(1, GetRoot(2), GetRoot(0), anchor, anchor),
            new FindHead(anchor, anchor, twoValidators, GetRoot(2)),
            new ProcessBlock(1, GetRoot(1), GetRoot(0), anchor, anchor),
            new FindHead(anchor, anchor, twoValidators, GetRoot(2)),
            new ProcessAttestation(0, GetRoot(1), 2),
            new FindHead(anchor, anchor, twoValidators, GetRoot(1)),
            new ProcessAttestation(1, GetRoot(2), 2),
            new FindHead(anchor, anchor, twoValidators, GetRoot(2)),
            new ProcessBlock(2, GetRoot(3), GetRoot(1), anchor, anchor),
            new FindHead(anchor, anchor, twoValidators, GetRoot(2)),
            new ProcessAttestation(0, GetRoot(3), 3),
            new FindHead(anchor, anchor, twoValidators, GetRoot(2)),
            new ProcessAttestation(1, GetRoot(1), 3),
            new FindHead(anchor, anchor, twoValidators, GetRoot(3)),
            new ProcessBlock(3, GetRoot(4), GetRoot(3), anchor, anchor),
            new FindHead(anchor, anchor, twoValidators, GetRoot(4)),
            new ProcessBlock(4, GetRoot(5), GetRoot(4), new(2, GetRoot(1)), new(2, GetRoot(1))),
            new FindHead(anchor, anchor, twoValidators, GetRoot(2)),
            new ProcessBlock(0, GetRoot(6), GetRoot(4), anchor, anchor),
            new ProcessAttestation(0, GetRoot(5), 4),
            new ProcessAttestation(1, GetRoot(5), 4),
            new ProcessBlock(0, GetRoot(7), GetRoot(5), justified5, justified5),
            new ProcessBlock(0, GetRoot(8), GetRoot(7), justified5, justified5),
            new ProcessBlock(0, GetRoot(9), GetRoot(8), justified5, justified5),
            new FindHead(anchor, anchor, twoValidators, GetRoot(6)),
            new FindHead(justified5, justified5, twoValidators, GetRoot(9)),
            new ProcessAttestation(0, GetRoot(9), 5),
            new ProcessAttestation(1, GetRoot(9), 5),
            new ProcessBlock(0, GetRoot(10), GetRoot(8), justified5, justified5),
            new FindHead(justified5, justified5, twoValidators, GetRoot(9)),
            new ProcessAttestation(2, GetRoot(10), 5),
            new ProcessAttestation(3, GetRoot(10), 5),
            new FindHead(justified5, justified5, fourValidators, GetRoot(10)),
            new FindHead(justified5, justified5, lastTwoExited, GetRoot(9)),
            new FindHead(justified5, justified5, fourValidators, GetRoot(10)),
            new FindHead(justified5, justified5, twoValidators, GetRoot(9)),
            new Prune(GetRoot(5), int.MaxValue, 11),
            new FindHead(justified5, justified5, twoValidators, GetRoot(9)),
            new Prune(GetRoot(5), 1, 6),
            new FindHead(justified5, justified5, twoValidators, GetRoot(9)),
            new ProcessBlock(0, GetRoot(11), GetRoot(9), justified5, justified5),
            new FindHead(justified5, justified5, twoValidators, GetRoot(11)),
        ];

        return ForkChoiceTestDefinition.Create(anchor, operations);
    }
}
