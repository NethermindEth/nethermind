// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class CompactionScheduleTests
{
    private static byte[] EncodedOffset(int value) => Rlp.Encode(value).Bytes;

    [Test]
    public void Constructor_NoStoredValue_GeneratesPersistsAndReturnsValueInRange()
    {
        MemDb metadataDb = new();
        FlatDbConfig config = new() { CompactSize = 32 };

        CompactionSchedule schedule = new(metadataDb, config, LimboLogs.Instance);

        Assert.That(schedule.Offset, Is.InRange(0, 31));
        byte[]? stored = metadataDb.Get(MetadataDbKeys.FlatDbCompactionOffset);
        Assert.That(stored, Is.Not.Null);
        Assert.That(stored.AsRlpValueContext().DecodeInt(), Is.EqualTo(schedule.Offset));
    }

    [Test]
    public void Constructor_StoredValidValue_ReturnsItWithoutOverwriting()
    {
        MemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.FlatDbCompactionOffset, EncodedOffset(7));
        byte[] before = metadataDb.Get(MetadataDbKeys.FlatDbCompactionOffset)!;
        FlatDbConfig config = new() { CompactSize = 32 };

        CompactionSchedule schedule = new(metadataDb, config, LimboLogs.Instance);

        Assert.That(schedule.Offset, Is.EqualTo(7));
        byte[] after = metadataDb.Get(MetadataDbKeys.FlatDbCompactionOffset)!;
        Assert.That(after, Is.EqualTo(before));
    }

    [Test]
    public void Constructor_RegenerateFlagTrue_OverwritesStoredOffset()
    {
        MemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.FlatDbCompactionOffset, EncodedOffset(7));
        FlatDbConfig config = new() { CompactSize = 32, RegenerateCompactionOffset = true };

        CompactionSchedule schedule = new(metadataDb, config, LimboLogs.Instance);

        Assert.That(schedule.Offset, Is.InRange(0, 31));
        int stored = metadataDb.Get(MetadataDbKeys.FlatDbCompactionOffset)!.AsRlpValueContext().DecodeInt();
        Assert.That(stored, Is.EqualTo(schedule.Offset));
    }

    [TestCase(-1)]
    [TestCase(32)]
    [TestCase(100)]
    public void Constructor_StoredOutOfRange_Regenerates(int badStored)
    {
        MemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.FlatDbCompactionOffset, EncodedOffset(badStored));
        FlatDbConfig config = new() { CompactSize = 32 };

        CompactionSchedule schedule = new(metadataDb, config, LimboLogs.Instance);

        Assert.That(schedule.Offset, Is.InRange(0, 31));
        int stored = metadataDb.Get(MetadataDbKeys.FlatDbCompactionOffset)!.AsRlpValueContext().DecodeInt();
        Assert.That(stored, Is.EqualTo(schedule.Offset));
    }

    [Test]
    public void Constructor_CompactSizeDisabled_OffsetIsZeroAndDbUntouched()
    {
        MemDb metadataDb = new();
        FlatDbConfig config = new() { CompactSize = 1, MinCompactSize = 0 };

        CompactionSchedule schedule = new(metadataDb, config, LimboLogs.Instance);

        Assert.That(schedule.Offset, Is.EqualTo(0));
        Assert.That(metadataDb.Get(MetadataDbKeys.FlatDbCompactionOffset), Is.Null);
    }

    [TestCase(2, 2)]
    [TestCase(4, 4)]
    [TestCase(6, 2)]
    [TestCase(8, 8)]
    [TestCase(10, 2)]
    [TestCase(12, 4)]
    [TestCase(14, 2)]
    [TestCase(16, 16)]
    [TestCase(32, 16)]   // capped at CompactSize=16
    public void GetCompactSize_OffsetZero_MatchesBitTrick(long blockNumber, int expected)
    {
        FlatDbConfig config = new() { CompactSize = 16, MinCompactSize = 2 };
        CompactionSchedule schedule = ScheduleHelper.CreateWithOffset(config, 0);

        Assert.That(schedule.GetCompactSize(blockNumber), Is.EqualTo(expected));
    }

    [TestCase(0, 1)]    // block 0 always 1
    [TestCase(13, 16)]  // 13+3 = 16 -> full
    [TestCase(16, 1)]   // 16+3 = 19 -> 19 & -19 = 1 (below min)
    [TestCase(5, 8)]    // 5+3 = 8
    [TestCase(29, 16)]  // 29+3 = 32 -> 32 & -32 = 32, capped at 16
    public void GetCompactSize_WithOffset3_ShiftsBoundaries(long blockNumber, int expected)
    {
        FlatDbConfig config = new() { CompactSize = 16, MinCompactSize = 2 };
        CompactionSchedule schedule = ScheduleHelper.CreateWithOffset(config, 3);

        Assert.That(schedule.GetCompactSize(blockNumber), Is.EqualTo(expected));
    }

    [Test]
    public void GetCompactSize_BelowMinCompactSize_ReturnsOne()
    {
        FlatDbConfig config = new() { CompactSize = 16, MinCompactSize = 4 };
        CompactionSchedule schedule = ScheduleHelper.CreateWithOffset(config, 0);

        // block 2 -> (2 & -2) = 2 < min 4
        Assert.That(schedule.GetCompactSize(2), Is.EqualTo(1));
        // block 4 -> (4 & -4) = 4 == min, allowed
        Assert.That(schedule.GetCompactSize(4), Is.EqualTo(4));
    }

    [TestCase(0, 0, 16)]    // from 0, offset 0 -> next full at 16
    [TestCase(16, 0, 32)]   // from boundary, advance by CompactSize
    [TestCase(15, 0, 16)]
    [TestCase(0, 3, 13)]    // from 0, offset 3 -> 0+(16-3) = 13
    [TestCase(13, 3, 29)]   // from boundary 13, advance by 16
    [TestCase(7, 5, 11)]    // from 7, offset 5 -> (7+5)%16=12, next at 7+(16-12)=11
    public void NextFullCompactionAfter_VariousOffsets(long from, int offset, long expected)
    {
        FlatDbConfig config = new() { CompactSize = 16 };
        CompactionSchedule schedule = ScheduleHelper.CreateWithOffset(config, offset);

        Assert.That(schedule.NextFullCompactionAfter(from), Is.EqualTo(expected));
    }

    [Test]
    public void NextFullCompactionAfter_CompactSizeDisabled_ReturnsLongMaxValue()
    {
        FlatDbConfig config = new() { CompactSize = 1, MinCompactSize = 0 };
        CompactionSchedule schedule = new(new MemDb(), config, LimboLogs.Instance);

        Assert.That(schedule.NextFullCompactionAfter(0), Is.EqualTo(long.MaxValue));
    }

    [Test]
    public void Constructor_NonPowerOf2CompactSize_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            new CompactionSchedule(new MemDb(), new FlatDbConfig { CompactSize = 10 }, LimboLogs.Instance));

    [Test]
    public void Constructor_NonPowerOf2MinCompactSize_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            new CompactionSchedule(new MemDb(), new FlatDbConfig { CompactSize = 16, MinCompactSize = 3 }, LimboLogs.Instance));

    [Test]
    public void Constructor_MinCompactSizeGreaterThanCompactSize_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            new CompactionSchedule(new MemDb(), new FlatDbConfig { CompactSize = 8, MinCompactSize = 16 }, LimboLogs.Instance));
}
