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
    [TestCase(16, 0, 0u, ExpectedResult = 1ul, TestName = "GetCompactSize_Genesis_DoesNotCompact")]
    [TestCase(16, 0, 1u, ExpectedResult = 1ul, TestName = "GetCompactSize_OddBlock_DoesNotCompact")]
    [TestCase(16, 0, 2u, ExpectedResult = 2ul)]
    [TestCase(16, 0, 4u, ExpectedResult = 4ul)]
    [TestCase(16, 0, 6u, ExpectedResult = 2ul)]
    [TestCase(16, 0, 8u, ExpectedResult = 8ul)]
    [TestCase(16, 0, 12u, ExpectedResult = 4ul)]
    [TestCase(16, 0, 16u, ExpectedResult = 16ul, TestName = "GetCompactSize_AtTheCap_IsTheCap")]
    [TestCase(16, 0, 32u, ExpectedResult = 16ul, TestName = "GetCompactSize_PastTheCap_StaysCapped")]
    [TestCase(16, 3, 16u, ExpectedResult = 1ul, TestName = "GetCompactSize_Offset_UnalignsBlock")]
    [TestCase(16, 3, 13u, ExpectedResult = 16ul)]
    [TestCase(1, 0, 16u, ExpectedResult = 1ul, TestName = "GetCompactSize_CompactionDisabled_DoesNotCompact")]
    public ulong GetCompactSize_IsTheLargestPowerOfTwoDividingTheShiftedBlock(int compactSize, long offset, ulong blockNumber) =>
        Schedule(compactSize, offset).GetCompactSize(blockNumber);

    /// <summary>Persistence targets the next full-width merge.</summary>
    [TestCase(16, 0, 0u, ExpectedResult = 16ul)]
    [TestCase(16, 0, 1u, ExpectedResult = 16ul)]
    [TestCase(16, 0, 16u, ExpectedResult = 32ul, TestName = "NextFullCompactionAfter_OnABoundary_IsTheNextOne")]
    [TestCase(16, 0, 17u, ExpectedResult = 32ul)]
    [TestCase(16, 0, ulong.MaxValue, ExpectedResult = 16ul, TestName = "NextFullCompactionAfter_PreGenesis_AnchorsAtGenesis")]
    [TestCase(16, 3, 0u, ExpectedResult = 13ul)]
    [TestCase(1, 0, 1u, ExpectedResult = ulong.MaxValue, TestName = "NextFullCompactionAfter_CompactionDisabled_HasNoBoundary")]
    public ulong NextFullCompactionAfter_IsTheNextFullWidthBoundary(int compactSize, long offset, ulong blockNumber) =>
        Schedule(compactSize, offset).NextFullCompactionAfter(new StateId(blockNumber, default));

    /// <summary>A generated offset persists across restarts.</summary>
    [Test]
    public void GeneratedOffset_IsPersistedAndReloaded()
    {
        MemDb metadataDb = new();
        PbtConfig config = new() { CompactSize = 16, CompactionOffset = -1 };

        PbtCompactionSchedule first = new(metadataDb, config, LimboLogs.Instance);
        PbtCompactionSchedule reopened = new(metadataDb, config, LimboLogs.Instance);

        Assert.That(reopened.NextFullCompactionAfter(new StateId(0, default)), Is.EqualTo(first.NextFullCompactionAfter(new StateId(0, default))));
    }

    /// <summary>Compaction sizes must be powers of two for the lowest-set-bit schedule to nest.</summary>
    [TestCase(3)]
    [TestCase(24)]
    public void NonPowerOfTwoCompactSize_Throws(int compactSize) =>
        Assert.Throws<ArgumentException>(() => Schedule(compactSize, offset: 0));

    private static PbtCompactionSchedule Schedule(int compactSize, long offset) =>
        new(new MemDb(), new PbtConfig { CompactSize = compactSize, CompactionOffset = offset }, LimboLogs.Instance);
}
