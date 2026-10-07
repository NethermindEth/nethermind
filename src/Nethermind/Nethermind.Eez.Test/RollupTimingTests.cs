// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Eez.Sequencer;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class RollupTimingTests
{
    private static readonly RollupTiming Mainnet = new(12_000, 2_000, 4_000, 1_500);
    private static readonly RollupTiming ChiadoSlow = new(4_000, 2_000, 1_500, 100);
    private static readonly RollupTiming ChiadoFast = new(5_000, 1_000, 200, 1_700);

    [TestCaseSource(nameof(Layouts))]
    public void Layout_ReferenceDeployments_MatchTheReferenceTable(RollupTiming timing, uint k, uint future, uint live, int windowOpenSeconds)
    {
        Assert.That(timing.FindViolation(), Is.Null, "precondition: the reference deployments are valid");

        Assert.That((timing.K, timing.FutureCount, timing.LiveCount, timing.ProofWindowOpen), Is.EqualTo((k, future, live, TimeSpan.FromSeconds(windowOpenSeconds))),
            "the slot layout the reference computes for the same timing");
    }

    [TestCaseSource(nameof(Violations))]
    public void FindViolation_TimingThatCannotComposeSlots_NamesTheRule(RollupTiming timing, string rule) =>
        Assert.That(timing.FindViolation(), Does.Contain(rule), "the reference refuses the same timing at startup");

    [TestCaseSource(nameof(Compositions))]
    public void Compose_HeadBelowTheSyncHeight_ProducesTheReferenceLayout(RollupTiming timing, ulong head, ulong syncHeight, ulong maxCatchup, SlotComposition expected) =>
        Assert.That(timing.Compose(head, syncHeight, maxCatchup), Is.EqualTo(expected), "the layout the reference composes from the same head");

    [Test]
    public void Compose_OffGridHead_RealignsOnTheNextSlot()
    {
        const ulong head = 174_197;
        const ulong sync = 174_198;
        Assert.That(head % Mainnet.K, Is.Not.Zero, "precondition: the head is off the slot grid");

        SlotComposition near = Mainnet.Compose(head, sync, 64);
        SlotComposition far = Mainnet.Compose(head, sync + Mainnet.K, RollupTiming.MaxBlocksPerCatchup);

        Assert.That(head + near.Blocks, Is.EqualTo(sync), "the next Sync block lands on the grid");
        Assert.That(far.Blocks, Is.EqualTo(Mainnet.K + 1UL), "one span longer than a slot realigns a head a slot further behind");
    }

    [Test]
    public void Compose_FarBehind_EndsEveryCatchupOnTheGridWithinTheCap()
    {
        const ulong sync = 6_000;
        foreach (ulong head in (ulong[])[0, 1, 5, 100, 137, 5_000, 5_699])
        {
            SlotComposition composition = Mainnet.Compose(head, sync, RollupTiming.MaxBlocksPerCatchup);
            if (composition.Kind != SlotCompositionKind.Catchup)
            {
                continue;
            }

            ulong terminal = head + composition.Blocks;
            Assert.That(terminal % Mainnet.K, Is.Zero, $"the catch-up from {head} ends on the grid");
            Assert.That(composition.Blocks, Is.LessThanOrEqualTo(RollupTiming.MaxBlocksPerCatchup), $"the catch-up from {head} stays within the cap");
            Assert.That(terminal, Is.LessThanOrEqualTo(sync), $"the catch-up from {head} never passes the Sync height");
        }
    }

    [Test]
    public void Compose_GenesisOffTheSlotBoundary_KeepsTheSyncHeightsResidue()
    {
        SlotComposition composition = Mainnet.Compose(0, 317, RollupTiming.MaxBlocksPerCatchup);

        Assert.That(composition.Kind, Is.EqualTo(SlotCompositionKind.Catchup), "precondition: the gap exceeds the cap");
        Assert.That((317 - composition.Blocks) % Mainnet.K, Is.Zero, "the catch-up steps back whole slots from the Sync height");
    }

    [TestCase(6UL, TestName = "OneSlot")]
    [TestCase(64UL, TestName = "SpeculativeRoom")]
    [TestCase(299UL, TestName = "BelowTheDefaultCap")]
    [TestCase(300UL, TestName = "DefaultCap")]
    public void Compose_CatchupBudget_IsNeverExceeded(ulong budget)
    {
        SlotComposition composition = Mainnet.Compose(0, 10_000, budget);

        Assert.That(composition.Kind, Is.EqualTo(SlotCompositionKind.Catchup), "precondition: far behind");
        Assert.That(composition.Blocks, Is.LessThanOrEqualTo(budget), "one catch-up stays within its budget");
    }

    [TestCase(0UL, 6_000UL, TestName = "OnGridSyncHeight")]
    [TestCase(137UL, 6_005UL, TestName = "OffsetGrid")]
    [TestCase(5_000UL, 6_000UL, TestName = "CursorCloseToTheCap")]
    public void HistoricalChunkBoundary_BacklogOverTheCap_IsOnTheGridWithinTheCap(ulong cursor, ulong syncHeight)
    {
        ulong? boundary = Mainnet.HistoricalChunkBoundary(cursor, syncHeight, RollupTiming.MaxBlocksPerCatchup);

        Assert.That(boundary, Is.Not.Null, "a backlog over the cap always has a boundary");
        Assert.That((syncHeight - boundary!.Value) % Mainnet.K, Is.Zero, "the boundary is a Sync height derivation rebuilds by position");
        Assert.That(boundary.Value - cursor, Is.InRange(1UL, RollupTiming.MaxBlocksPerCatchup), "the chunk settles something and stays within the cap");
    }

    [TestCase(100UL, 400UL, 300UL, null, TestName = "BacklogFitsTheCap")]
    [TestCase(100UL, 100UL, 300UL, null, TestName = "NoBacklog")]
    [TestCase(97UL, 6_000UL, 3UL, null, TestName = "CapBelowOneSlot")]
    [TestCase(97UL, 6_000UL, 6UL, 102UL, TestName = "CapOfOneSlot")]
    public void HistoricalChunkBoundary_EdgeCases_MatchTheReference(ulong cursor, ulong syncHeight, ulong cap, ulong? expected) =>
        Assert.That(Mainnet.HistoricalChunkBoundary(cursor, syncHeight, cap), Is.EqualTo(expected), "the boundary the reference computes");

    private static TestCaseData[] Layouts() =>
    [
        new(Mainnet, 6u, 2u, 3u, 6) { TestName = "Mainnet" },
        new(ChiadoSlow, 2u, 0u, 1u, 2) { TestName = "ChiadoSlow" },
        new(ChiadoFast, 5u, 1u, 3u, 3) { TestName = "ChiadoFast" },
        new(new RollupTiming(5_000, 1_000, 2_500, 1_300), 5u, 3u, 1u, 1) { TestName = "ChiadoDeployed" },
        new(new RollupTiming(12_000, 2_000, 2_000, 1_000), 6u, 1u, 4u, 8) { TestName = "KurtosisDeployed" },
    ];

    private static TestCaseData[] Violations() =>
    [
        new(new RollupTiming(12_000, 5_000, 2_000, 100), "multiple of the L2 block time") { TestName = "NonIntegerK" },
        new(new RollupTiming(5_000, 2_500, 2_000, 100), "whole seconds") { TestName = "SubSecondL2" },
        new(new RollupTiming(2_000, 2_000, 500, 100), "at least 2 L2 blocks") { TestName = "OneBlockSlot" },
        new(new RollupTiming(12_000, 2_000, 10_000, 3_000), "below the L1 block time") { TestName = "ProofLongerThanTheSlot" },
        new(new RollupTiming(4_000, 2_000, 3_000, 100), "of blocks before the Sync block") { TestName = "ProofLongerThanTheFutureRegion" },
        new(new RollupTiming(12_000, 2_000, 1_900, 100), "to compose the Sync block in") { TestName = "NoTimeLeftToComposeTheSyncBlock" },
        new(new RollupTiming(0, 2_000, 4_000, 100), "must be positive") { TestName = "ZeroL1" },
        new(new RollupTiming(12_000, 0, 4_000, 100), "must be positive") { TestName = "ZeroL2" },
        new(new RollupTiming(12_000, 2_000, 0, 100), "must be positive") { TestName = "ZeroProof" },
    ];

    private static TestCaseData[] Compositions() =>
    [
        new(Mainnet, 3UL, 6UL, RollupTiming.MaxBlocksPerCatchup, SlotComposition.Slot(0, 2)) { TestName = "AtTheLiveRegionEnd" },
        new(Mainnet, 2UL, 6UL, RollupTiming.MaxBlocksPerCatchup, SlotComposition.Slot(1, 2)) { TestName = "OneLiveBehind" },
        new(Mainnet, 0UL, 6UL, RollupTiming.MaxBlocksPerCatchup, SlotComposition.Slot(3, 2)) { TestName = "StartOfSlot" },
        new(Mainnet, 5UL, 6UL, RollupTiming.MaxBlocksPerCatchup, SlotComposition.Slot(0, 0)) { TestName = "InsideTheFutureRegion" },
        new(Mainnet, 0UL, 12UL, RollupTiming.MaxBlocksPerCatchup, SlotComposition.Catchup(11)) { TestName = "CatchupWithinTheCap" },
        new(Mainnet, 0UL, 318UL, RollupTiming.MaxBlocksPerCatchup, SlotComposition.Catchup(299)) { TestName = "CatchupClampedToTheGrid" },
        new(Mainnet, 12UL, 18UL, RollupTiming.MaxBlocksPerCatchup, SlotComposition.Slot(3, 2)) { TestName = "SlotAfterACatchup" },
        new(Mainnet, 60UL, 6_000UL, 4UL, SlotComposition.Idle) { TestName = "BudgetBelowOneSlot" },
        new(Mainnet, 6UL, 6UL, RollupTiming.MaxBlocksPerCatchup, SlotComposition.Idle) { TestName = "AtTheSyncHeight" },
        new(Mainnet, 7UL, 6UL, RollupTiming.MaxBlocksPerCatchup, SlotComposition.Idle) { TestName = "PastTheSyncHeight" },
        new(ChiadoSlow, 0UL, 2UL, RollupTiming.MaxBlocksPerCatchup, SlotComposition.Slot(1, 0)) { TestName = "ChiadoSlowStartOfSlot" },
        new(ChiadoFast, 0UL, 5UL, RollupTiming.MaxBlocksPerCatchup, SlotComposition.Slot(3, 1)) { TestName = "ChiadoFastStartOfSlot" },
    ];
}
