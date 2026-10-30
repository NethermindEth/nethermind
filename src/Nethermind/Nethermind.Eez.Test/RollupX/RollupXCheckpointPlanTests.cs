// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class RollupXCheckpointPlanTests
{
    [TestCase("", 0, new[] { -1 }, TestName = "Create_EmptyTerminal_SelectsPreExecutionCheckpoint")]
    [TestCase("UU", 2, new[] { 1 }, TestName = "Create_OrdinaryOnly_UsesCompleteOrdinaryPrefix")]
    [TestCase("N", 0, new[] { -1, 0 }, TestName = "Create_InboundOnly_KeepsEmptyPrefixAndEffect")]
    [TestCase("NU", 0, new[] { -1, 1 }, TestName = "Create_OutboundPair_CheckpointsAfterUser")]
    [TestCase("UUNUN", 2, new[] { 1, 3, 4 }, TestName = "Create_OrdinaryThenEffects_PreservesPrefixAndEffectOrder")]
    [TestCase("UNNN", 1, new[] { 0, 1, 2, 3 }, TestName = "Create_ConsecutiveInbound_CheckpointsEveryEffect")]
    public void Create_ExecutedTransactionLayout_SelectsIndependentBoundaries(string layout, int ordinary, int[] expected)
    {
        RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(Terminal(layout));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.OrdinaryTransactions, Is.EqualTo(ordinary), "ordinary transactions belong to H[0], not the effect suffix");
            Assert.That(plan.Positions.ToArray(), Is.EqualTo(expected), "checkpoint positions come only from the terminal transaction layout");
        }
    }

    [TestCase(-1, TestName = "Validate_MissingPrefixCheckpoint_RejectsBackendOutput")]
    [TestCase(0, TestName = "Validate_WrongOrdinaryPosition_RejectsBackendOutput")]
    [TestCase(1, TestName = "Validate_WrongEffectPosition_RejectsBackendOutput")]
    [TestCase(2, TestName = "Validate_WrongTerminalPosition_RejectsBackendOutput")]
    public void Validate_CheckpointsDifferFromLayout_IsInternalInvariant(int malformedIndex)
    {
        RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(Terminal("UUNUN"));
        EezTransactionCheckpoint[] checkpoints = Checkpoints();
        if (malformedIndex >= 0) checkpoints[malformedIndex] = checkpoints[malformedIndex] with { TransactionIndex = -1 };
        else checkpoints = checkpoints[1..];

        EezSettlementException failure = Assert.Throws<EezSettlementException>(() => plan.Validate(checkpoints))!;

        Assert.That(failure.Failure, Is.EqualTo(EezSettlementFailure.InternalInvariant), "the execution backend must return every selected prefix at its exact position");
    }

    [Test]
    public void ObservationPositions_ValidatedRoots_MapToExactPrefixAndTerminal()
    {
        RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(Terminal("UUNUN"));
        Hash256[] pins = [TestItem.KeccakA, TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC, TestItem.KeccakD];

        int[] positions = plan.ObservationPositions(pins, Checkpoints(), TestItem.KeccakD, 5);

        Assert.That(positions, Is.EqualTo(new[] { 2, 2, 4, 5, 5 }), "FIFO reads bind to their independent ordinary/effect carrier or final terminal hash");
    }

    [Test]
    public void ObservationPositions_UnknownPinnedRoot_RejectsClaim()
    {
        RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(Terminal("UUNUN"));

        Assert.Throws<EezSettlementException>(() => plan.ObservationPositions([TestItem.KeccakE], Checkpoints(), TestItem.KeccakD, 5),
            "a caller cannot choose an unexecuted state by supplying a hash");
    }

    [Test]
    public void ObservationPositions_RootsMoveBackwards_RejectsClaim()
    {
        RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(Terminal("UUNUN"));

        Assert.Throws<EezSettlementException>(() => plan.ObservationPositions([TestItem.KeccakB, TestItem.KeccakA], Checkpoints(), TestItem.KeccakD, 5),
            "a static read cannot move back to state before an earlier applied mutation");
    }

    [Test]
    public void ObservationPositions_TransactionCountDiffersFromPlan_RejectsBackendOutput()
    {
        RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(Terminal("UUNUN"));

        EezSettlementException failure = Assert.Throws<EezSettlementException>(() => plan.ObservationPositions([], Checkpoints(), TestItem.KeccakD, 6))!;

        Assert.That(failure.Failure, Is.EqualTo(EezSettlementFailure.InternalInvariant), "a terminal hash cannot authorize an unvalidated transaction suffix");
    }

    private static Block Terminal(string layout) => Build.A.Block.WithTransactions(layout.Select(type => type == 'N'
        ? Build.A.Transaction.WithType(EezConstants.SystemTxType).TestObject : Build.A.Transaction.TestObject).ToArray()).TestObject;

    private static EezTransactionCheckpoint[] Checkpoints() =>
    [
        new(1, TestItem.KeccakD, TestItem.KeccakA),
        new(3, TestItem.KeccakD, TestItem.KeccakB),
        new(4, TestItem.KeccakD, TestItem.KeccakC),
    ];
}
