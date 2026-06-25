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
    private static byte[] EncodedOffset(long value) => Rlp.Encode(value).Bytes;

    [Test]
    public void Constructor_NoStoredValue_GeneratesPersistsAndReturnsValueInRange()
    {
        MemDb metadataDb = new();
        FlatDbConfig config = new() { CompactSize = 32 };

        CompactionSchedule schedule = new(metadataDb, config, LimboLogs.Instance);

        Assert.That(schedule.Offset, Is.InRange(0UL, (ulong)int.MaxValue - 1));
        byte[]? stored = metadataDb.Get(MetadataDbKeys.FlatDbCompactionOffset);
        Assert.That(stored, Is.Not.Null);
        Assert.That(new RlpReader(stored).DecodeLong(), Is.EqualTo(schedule.Offset));
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
    public void Constructor_ConfiguredOffset_UsedWithoutTouchingDb()
    {
        MemDb metadataDb = new();
        FlatDbConfig config = new() { CompactSize = 32, CompactionOffset = 7 };

        CompactionSchedule schedule = new(metadataDb, config, LimboLogs.Instance);

        Assert.That(schedule.Offset, Is.EqualTo(7));
        Assert.That(metadataDb.Get(MetadataDbKeys.FlatDbCompactionOffset), Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Constructor_ConfiguredOffset_TakesPrecedenceOverStoredValue(bool regenerateFlag)
    {
        MemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.FlatDbCompactionOffset, EncodedOffset(5));
        FlatDbConfig config = new() { CompactSize = 32, CompactionOffset = 7, RegenerateCompactionOffset = regenerateFlag };

        CompactionSchedule schedule = new(metadataDb, config, LimboLogs.Instance);

        Assert.That(schedule.Offset, Is.EqualTo(7));
        long stored = new RlpReader(metadataDb.Get(MetadataDbKeys.FlatDbCompactionOffset)!).DecodeLong();
        Assert.That(stored, Is.EqualTo(5), "configured offset should not modify the stored offset");
    }

    [TestCase(1_000_000L)]
    [TestCase(int.MaxValue - 1L)]
    public void Constructor_StoredLargePositiveValue_KeptAsIs(long stored)
    {
        MemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.FlatDbCompactionOffset, EncodedOffset(stored));
        FlatDbConfig config = new() { CompactSize = 32 };

        CompactionSchedule schedule = new(metadataDb, config, LimboLogs.Instance);

        Assert.That(schedule.Offset, Is.EqualTo(stored));
    }

    [Test]
    public void Constructor_RegenerateFlagTrue_OverwritesStoredOffset()
    {
        MemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.FlatDbCompactionOffset, EncodedOffset(7));
        FlatDbConfig config = new() { CompactSize = 32, RegenerateCompactionOffset = true };

        CompactionSchedule schedule = new(metadataDb, config, LimboLogs.Instance);

        Assert.That(schedule.Offset, Is.InRange(0UL, (ulong)int.MaxValue - 1));
        long stored = new RlpReader(metadataDb.Get(MetadataDbKeys.FlatDbCompactionOffset)!).DecodeLong();
        Assert.That(stored, Is.EqualTo(schedule.Offset));
    }

    [TestCase(-1L)]
    [TestCase(-100L)]
    public void Constructor_StoredNegative_Regenerates(long badStored)
    {
        MemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.FlatDbCompactionOffset, EncodedOffset(badStored));
        FlatDbConfig config = new() { CompactSize = 32 };

        CompactionSchedule schedule = new(metadataDb, config, LimboLogs.Instance);

        Assert.That(schedule.Offset, Is.InRange(0UL, (ulong)int.MaxValue - 1));
        long stored = new RlpReader(metadataDb.Get(MetadataDbKeys.FlatDbCompactionOffset)!).DecodeLong();
        Assert.That(stored, Is.EqualTo(schedule.Offset));
    }

    [Test]
    public void Constructor_CompactSizeDisabled_OffsetIsZeroAndDbUntouched()
    {
        MemDb metadataDb = new();
        FlatDbConfig config = new() { CompactSize = 1 };

        CompactionSchedule schedule = new(metadataDb, config, LimboLogs.Instance);

        Assert.That(schedule.Offset, Is.EqualTo(0));
        Assert.That(metadataDb.Get(MetadataDbKeys.FlatDbCompactionOffset), Is.Null);
    }

    [TestCase(1UL, 1)]    // odd block: 1 & -1 = 1
    [TestCase(2UL, 2)]
    [TestCase(4UL, 4)]
    [TestCase(6UL, 2)]
    [TestCase(8UL, 8)]
    [TestCase(10UL, 2)]
    [TestCase(12UL, 4)]
    [TestCase(14UL, 2)]
    [TestCase(16UL, 16)]
    [TestCase(32UL, 16)]   // capped at CompactSize=16
    public void GetCompactSize_OffsetZero_MatchesBitTrick(ulong blockNumber, int expected)
    {
        FlatDbConfig config = new() { CompactSize = 16 };
        CompactionSchedule schedule = ScheduleHelper.CreateWithOffset(config, 0);

        Assert.That(schedule.GetCompactSize(blockNumber), Is.EqualTo(expected));
    }

    [TestCase(0UL, 1)]    // block 0 always 1
    [TestCase(13UL, 16)]  // 13+3 = 16 -> full
    [TestCase(16UL, 1)]   // 16+3 = 19 -> 19 & -19 = 1 (caller treats as no compaction)
    [TestCase(5UL, 8)]    // 5+3 = 8
    [TestCase(29UL, 16)]  // 29+3 = 32 -> 32 & -32 = 32, capped at 16
    public void GetCompactSize_WithOffset3_ShiftsBoundaries(ulong blockNumber, int expected)
    {
        FlatDbConfig config = new() { CompactSize = 16 };
        CompactionSchedule schedule = ScheduleHelper.CreateWithOffset(config, 3);

        Assert.That(schedule.GetCompactSize(blockNumber), Is.EqualTo(expected));
    }

    [TestCase(3L, 19L)]      // offset 3 and 3+16=19 should be equivalent
    [TestCase(3L, 35L)]      // offset 3 and 3+32=35 should be equivalent
    [TestCase(7L, 1_000_007L)] // large offsets work modulo CompactSize
    public void GetCompactSize_OffsetLargerThanCompactSize_EquivalentToOffsetModCompactSize(long smallOffset, long largeOffset)
    {
        FlatDbConfig config = new() { CompactSize = 16 };
        CompactionSchedule small = ScheduleHelper.CreateWithOffset(config, smallOffset);
        CompactionSchedule large = ScheduleHelper.CreateWithOffset(config, largeOffset);

        for (ulong block = 1UL; block <= 64UL; block++)
        {
            Assert.That(large.GetCompactSize(block), Is.EqualTo(small.GetCompactSize(block)),
                $"Tier mismatch at block {block} between offset {smallOffset} and {largeOffset}");
            Assert.That(large.NextFullCompactionAfter(block), Is.EqualTo(small.NextFullCompactionAfter(block)),
                $"Next boundary mismatch from block {block} between offset {smallOffset} and {largeOffset}");
        }
    }

    [TestCase(0UL, 0, 16UL)]    // from 0, offset 0 -> next full at 16
    [TestCase(16UL, 0, 32UL)]   // from boundary, advance by CompactSize
    [TestCase(15UL, 0, 16UL)]
    [TestCase(0UL, 3, 13UL)]    // from 0, offset 3 -> 0+(16-3) = 13
    [TestCase(13UL, 3, 29UL)]   // from boundary 13, advance by 16
    [TestCase(7UL, 5, 11UL)]    // from 7, offset 5 -> (7+5)%16=12, next at 7+(16-12)=11
    public void NextFullCompactionAfter_VariousOffsets(ulong from, int offset, ulong expected)
    {
        FlatDbConfig config = new() { CompactSize = 16 };
        CompactionSchedule schedule = ScheduleHelper.CreateWithOffset(config, offset);

        Assert.That(schedule.NextFullCompactionAfter(from), Is.EqualTo(expected));
    }

    [Test]
    public void NextFullCompactionAfter_CompactSizeDisabled_ReturnsLongMaxValue()
    {
        FlatDbConfig config = new() { CompactSize = 1 };
        CompactionSchedule schedule = new(new MemDb(), config, LimboLogs.Instance);

        Assert.That(schedule.NextFullCompactionAfter(0), Is.EqualTo(ulong.MaxValue));
    }

    [Test]
    public void Constructor_NonPowerOf2CompactSize_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            new CompactionSchedule(new MemDb(), new FlatDbConfig { CompactSize = 10 }, LimboLogs.Instance));

    [TestCase(0, 0, 8192, false)]   // block 0 → size 1
    [TestCase(0, 16, 8192, false)]  // exactly CompactSize — not "large"
    [TestCase(0, 8, 8192, false)]   // intermediate (< CompactSize)
    [TestCase(0, 32, 8192, true)]   // 2× CompactSize
    [TestCase(0, 64, 8192, true)]   // 4×
    [TestCase(3, 13, 8192, false)]  // (13+3) = 16, exactly CompactSize
    [TestCase(3, 16, 8192, false)]  // (16+3) = 19, alignment 1
    [TestCase(3, 29, 8192, true)]   // (29+3) = 32, > CompactSize
    [TestCase(0, 32, 16, false)]    // max == CompactSize: alignment 32 capped to 16 → not large
    public void IsLargeCompactionBoundary_TrueWhenWindowExceedsCompactSize(int offset, int blockNumber, int maxCompactSize, bool expected)
    {
        FlatDbConfig config = new() { CompactSize = 16, PersistedSnapshotMaxCompactSize = (ulong)maxCompactSize };
        CompactionSchedule schedule = ScheduleHelper.CreateWithOffset(config, offset);

        Assert.That(schedule.IsLargeCompactionBoundary((ulong)blockNumber), Is.EqualTo(expected));
    }

    [TestCase(0, 0, 8192, 1L)]      // block 0 → 1
    [TestCase(0, 16, 8192, 16L)]    // natural CompactSize boundary
    [TestCase(0, 32, 8192, 32L)]    // tier above CompactSize, below cap
    [TestCase(0, 48, 8192, 16L)]    // 48 & -48 = 16
    [TestCase(0, 64, 8192, 64L)]    // 4×, below cap
    [TestCase(3, 13, 8192, 16L)]    // shifted: (13+3) & -(13+3) = 16
    [TestCase(3, 29, 8192, 32L)]    // shifted: 32 (above CompactSize=16)
    [TestCase(0, 64, 32, 32L)]      // raw alignment 64 capped at PersistedSnapshotMaxCompactSize=32
    [TestCase(0, 128, 32, 32L)]     // raw alignment 128 capped at 32
    public void GetPersistedSnapshotCompactSize_CappedAndOffsetAware(int offset, int blockNumber, int maxCompactSize, long expected)
    {
        FlatDbConfig config = new() { CompactSize = 16, PersistedSnapshotMaxCompactSize = (ulong)maxCompactSize };
        CompactionSchedule schedule = ScheduleHelper.CreateWithOffset(config, offset);

        Assert.That((long)schedule.GetPersistedSnapshotCompactSize((ulong)blockNumber), Is.EqualTo(expected));
    }

    [TestCase(0, 0, 8192, false)]   // block 0 → size 1
    [TestCase(0, 16, 8192, true)]   // exactly CompactSize
    [TestCase(0, 48, 8192, true)]   // 48 & -48 = 16
    [TestCase(0, 8, 8192, false)]   // intermediate (< CompactSize)
    [TestCase(0, 32, 8192, false)]  // large (> CompactSize)
    [TestCase(0, 64, 8192, false)]  // large
    [TestCase(3, 13, 8192, true)]   // shifted: (13+3) = 16
    [TestCase(3, 29, 8192, false)]  // shifted large: 32
    [TestCase(0, 32, 16, true)]     // max == CompactSize: alignment 32 capped to 16, exactly equals CompactSize
    public void IsCompactSizeBoundary_TrueOnlyWhenWindowEqualsCompactSize(int offset, int blockNumber, int maxCompactSize, bool expected)
    {
        FlatDbConfig config = new() { CompactSize = 16, PersistedSnapshotMaxCompactSize = (ulong)maxCompactSize };
        CompactionSchedule schedule = ScheduleHelper.CreateWithOffset(config, offset);

        Assert.That(schedule.IsCompactSizeBoundary((ulong)blockNumber), Is.EqualTo(expected));
    }
}
