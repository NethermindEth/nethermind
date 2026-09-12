// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtCompactionScheduleTests
{
    /// <summary>Verifies that compaction widths are nested powers of two, capped at <c>CompactSize</c>.</summary>
    [TestCase(0u, ExpectedResult = 1ul, TestName = "GetCompactSize_Genesis_DoesNotCompact")]
    [TestCase(1u, ExpectedResult = 1ul, TestName = "GetCompactSize_OddBlock_DoesNotCompact")]
    [TestCase(2u, ExpectedResult = 2ul)]
    [TestCase(4u, ExpectedResult = 4ul)]
    [TestCase(6u, ExpectedResult = 2ul)]
    [TestCase(8u, ExpectedResult = 8ul)]
    [TestCase(12u, ExpectedResult = 4ul)]
    [TestCase(16u, ExpectedResult = 16ul, TestName = "GetCompactSize_AtTheCap_IsTheCap")]
    [TestCase(32u, ExpectedResult = 16ul, TestName = "GetCompactSize_PastTheCap_StaysCapped")]
    public ulong GetCompactSize_IsTheLargestPowerOfTwoDividingTheBlock(ulong blockNumber) =>
        Schedule(compactSize: 16, offset: 0).GetCompactSize(blockNumber);

    /// <summary>Persistence targets the next full-width merge.</summary>
    [TestCase(0u, ExpectedResult = 16ul)]
    [TestCase(1u, ExpectedResult = 16ul)]
    [TestCase(16u, ExpectedResult = 32ul, TestName = "NextFullCompactionAfter_OnABoundary_IsTheNextOne")]
    [TestCase(17u, ExpectedResult = 32ul)]
    public ulong NextFullCompactionAfter_IsTheNextFullWidthBoundary(ulong blockNumber) =>
        Schedule(compactSize: 16, offset: 0).NextFullCompactionAfter(new StateId(blockNumber, default));

    /// <summary>An empty database targets the first post-genesis boundary.</summary>
    [Test]
    public void NextFullCompactionAfter_PreGenesis_AnchorsAtGenesis() =>
        Assert.That(Schedule(compactSize: 16, offset: 0).NextFullCompactionAfter(StateId.PreGenesis), Is.EqualTo(16ul));

    /// <summary>Offsets distribute compaction and persistence across blocks.</summary>
    [Test]
    public void Offset_ShiftsEveryBoundary()
    {
        PbtCompactionSchedule shifted = Schedule(compactSize: 16, offset: 3);

        Assert.That(shifted.GetCompactSize(16), Is.EqualTo(1ul), "block 16 is no longer aligned");
        Assert.That(shifted.GetCompactSize(13), Is.EqualTo(16ul), "13 + 3 is");
        Assert.That(shifted.NextFullCompactionAfter(new StateId(0, default)), Is.EqualTo(13ul));
    }

    /// <summary>A generated offset persists across restarts.</summary>
    [Test]
    public void GeneratedOffset_IsPersistedAndReloaded()
    {
        MemDb metadataDb = new();
        PbtConfig config = new() { CompactSize = 16, CompactionOffset = -1 };

        PbtCompactionSchedule first = new(metadataDb, config, LimboLogs.Instance);
        PbtCompactionSchedule reopened = new(metadataDb, config, LimboLogs.Instance);

        Assert.That(reopened.Offset, Is.EqualTo(first.Offset));
    }

    /// <summary>Compaction sizes must be powers of two for the lowest-set-bit schedule to nest.</summary>
    [TestCase(3)]
    [TestCase(24)]
    public void NonPowerOfTwoCompactSize_Throws(int compactSize) =>
        Assert.Throws<ArgumentException>(() => Schedule(compactSize, offset: 0));

    /// <summary><c>CompactSize</c> 1 disables compaction without being rejected.</summary>
    [Test]
    public void CompactionDisabled_HasNoBoundaries()
    {
        PbtCompactionSchedule disabled = Schedule(compactSize: 1, offset: 0);

        Assert.That(disabled.GetCompactSize(16), Is.EqualTo(1ul));
        Assert.That(disabled.NextFullCompactionAfter(new StateId(1, default)), Is.EqualTo(ulong.MaxValue));
    }

    private static PbtCompactionSchedule Schedule(int compactSize, long offset) =>
        new(new MemDb(), new PbtConfig { CompactSize = compactSize, CompactionOffset = offset }, LimboLogs.Instance);
}
