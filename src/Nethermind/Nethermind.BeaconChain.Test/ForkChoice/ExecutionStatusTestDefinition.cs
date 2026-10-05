// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.ForkChoice;
using static Nethermind.BeaconChain.Test.ForkChoice.TestHashes;

namespace Nethermind.BeaconChain.Test.ForkChoice;

public static class ExecutionStatusTestDefinition
{
    private static readonly CheckpointRef Anchor = new(1, GetRoot(0));

    private static FindHead Head(ulong[] balances, ulong expectedHead) => new(Anchor, Anchor, balances, GetRoot(expectedHead));

    private static List<Operation> InitialForkAndVote(ulong[] balances) =>
    [
        Head(balances, 0),
        new ProcessBlock(1, GetRoot(2), GetRoot(0), Anchor, Anchor),
        Head(balances, 2),
        new ProcessBlock(1, GetRoot(1), GetRoot(0), Anchor, Anchor),
        Head(balances, 2),
        new ProcessAttestation(0, GetRoot(1), 2),
        Head(balances, 1),
    ];

    private static List<Operation> CommonPrologue(ulong[] balances) =>
    [
        .. InitialForkAndVote(balances),
        new AssertWeight(GetRoot(0), 1),
        new AssertWeight(GetRoot(1), 1),
        new AssertWeight(GetRoot(2), 0),
        new ProcessAttestation(1, GetRoot(2), 2),
        Head(balances, 2),
        new AssertWeight(GetRoot(0), 2),
        new AssertWeight(GetRoot(1), 1),
        new AssertWeight(GetRoot(2), 1),
        new ProcessBlock(2, GetRoot(3), GetRoot(1), Anchor, Anchor),
        Head(balances, 2),
        new AssertWeight(GetRoot(0), 2),
        new AssertWeight(GetRoot(1), 1),
        new AssertWeight(GetRoot(2), 1),
        new AssertWeight(GetRoot(3), 0),
        new ProcessAttestation(0, GetRoot(3), 3),
    ];

    public static ForkChoiceTestDefinition Get01()
    {
        ulong[] balances = [1, 1];

        List<Operation> operations = CommonPrologue(balances);
        operations.AddRange(
        [
            Head(balances, 2),
            new AssertWeight(GetRoot(0), 2),
            new AssertWeight(GetRoot(1), 1),
            new AssertWeight(GetRoot(2), 1),
            new AssertWeight(GetRoot(3), 1),
            new InvalidatePayload(GetRoot(3), GetRoot(1)),
            Head(balances, 2),
            // Invalidation of 3 should have removed its weight upstream.
            new AssertWeight(GetRoot(0), 1),
            new AssertWeight(GetRoot(1), 0),
            new AssertWeight(GetRoot(2), 1),
            new AssertWeight(GetRoot(3), 0),
            new ProcessAttestation(1, GetRoot(1), 3),
            Head(balances, 1),
            new AssertWeight(GetRoot(0), 1),
            new AssertWeight(GetRoot(1), 1),
            new AssertWeight(GetRoot(2), 0),
            new AssertWeight(GetRoot(3), 0),
        ]);

        return ForkChoiceTestDefinition.Create(Anchor, operations);
    }

    public static ForkChoiceTestDefinition Get02()
    {
        ulong[] balances = [1, 1];

        List<Operation> operations = CommonPrologue(balances);
        operations.AddRange(
        [
            new ProcessAttestation(1, GetRoot(3), 3),
            Head(balances, 3),
            new AssertWeight(GetRoot(0), 2),
            new AssertWeight(GetRoot(1), 2),
            new AssertWeight(GetRoot(2), 0),
            new AssertWeight(GetRoot(3), 2),
            new InvalidatePayload(GetRoot(3), GetRoot(1)),
            Head(balances, 2),
            // Invalidation of 3 should have removed all weight (both votes were on its chain).
            new AssertWeight(GetRoot(0), 0),
            new AssertWeight(GetRoot(1), 0),
            new AssertWeight(GetRoot(2), 0),
            new AssertWeight(GetRoot(3), 0),
        ]);

        return ForkChoiceTestDefinition.Create(Anchor, operations);
    }

    public static ForkChoiceTestDefinition Get03()
    {
        ulong[] balances = Enumerable.Repeat(1_000UL, 2_000).ToArray();

        List<Operation> operations =
        [
            .. InitialForkAndVote(balances),
            new AssertWeight(GetRoot(0), 1_000),
            new AssertWeight(GetRoot(1), 1_000),
            new AssertWeight(GetRoot(2), 0),
            new ProcessAttestation(1, GetRoot(1), 2),
            Head(balances, 1),
            new AssertWeight(GetRoot(0), 2_000),
            new AssertWeight(GetRoot(1), 2_000),
            new AssertWeight(GetRoot(2), 0),
            new ProcessBlock(2, GetRoot(3), GetRoot(1), Anchor, Anchor),
            new FindHead(Anchor, Anchor, balances, GetRoot(3), ProposerBoostRoot: GetRoot(3)),
            new AssertWeight(GetRoot(0), 33_250),
            new AssertWeight(GetRoot(1), 33_250),
            new AssertWeight(GetRoot(2), 0),
            // A "magic number" from calculate_committee_fraction: 2_000_000 / 32 * 50%.
            new AssertWeight(GetRoot(3), 31_250),
            new InvalidatePayload(GetRoot(3), GetRoot(1)),
            new FindHead(Anchor, Anchor, balances, GetRoot(1), ProposerBoostRoot: GetRoot(3)),
            new AssertWeight(GetRoot(0), 2_000),
            new AssertWeight(GetRoot(1), 2_000),
            new AssertWeight(GetRoot(2), 0),
            new AssertWeight(GetRoot(3), 0),
        ];

        return ForkChoiceTestDefinition.Create(Anchor, operations);
    }
}
