// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class HistoryAvailabilityTests
{
    private const ulong MarkedBlock = 7;

    private TempPath _directory = null!;
    private ColumnsDb<FlatHistoryColumns> _columns = null!;
    private HistoryAvailability _availability = null!;

    private IDb AvailableBlocks => _columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks);

    [SetUp]
    public void SetUp()
    {
        _directory = TempPath.GetTempDirectory();
        _columns = new ColumnsDb<FlatHistoryColumns>(_directory.Path, new DbSettings(nameof(HistoryAvailabilityTests), _directory.Path), new DbConfig(),
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
            LimboLogs.Instance, Enum.GetValues<FlatHistoryColumns>());
        using (IWriteBatch batch = AvailableBlocks.StartWriteBatch())
        {
            HistoryAvailability.MarkBlock(batch, MarkedBlock, TestItem.KeccakA, HistoryAvailability.FormatVersion);
        }

        _availability = new HistoryAvailability(AvailableBlocks);
    }

    [TearDown]
    public void TearDown()
    {
        _columns.Dispose();
        _directory.Dispose();
    }

    [Test]
    public void RootMatches_ReadsTheMarkerWithoutAllocating()
    {
        const int Reads = 1000;
        bool matched = true;
        for (int i = 0; i < 10; i++) matched &= _availability.RootMatches(MarkedBlock, TestItem.KeccakA);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Reads; i++) matched &= _availability.RootMatches(MarkedBlock, TestItem.KeccakA);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(matched, Is.True, "precondition: the marker is read and matches");
            Assert.That(allocated, Is.LessThan(Reads), "the marker is compared where the database holds it, not copied into a new array per read");
        }
    }

    [Test]
    public void RootMatches_OnlyForTheCapturedRootOfAMarkerOfRootLength()
    {
        Span<byte> key = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(key, MarkedBlock + 1);
        AvailableBlocks.PutSpan(key, new byte[Hash256.Size + 1]);
        BinaryPrimitives.WriteUInt64BigEndian(key, MarkedBlock + 3);
        AvailableBlocks.PutSpan(key, new byte[Hash256.Size - 1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_availability.RootMatches(MarkedBlock, TestItem.KeccakA), Is.True);
            Assert.That(_availability.RootMatches(MarkedBlock, TestItem.KeccakB), Is.False, "another root");
            Assert.That(_availability.RootMatches(MarkedBlock + 1, Keccak.Zero), Is.False, "a marker longer than a root fails closed instead of throwing");
            Assert.That(_availability.RootMatches(MarkedBlock + 2, TestItem.KeccakA), Is.False, "no marker");
            Assert.That(_availability.RootMatches(MarkedBlock + 3, Keccak.Zero), Is.False, "a marker shorter than a root");
        }
    }

    [Test]
    public void IsCoveredAndRootMatches_RepeatedForACoveredBlock_ReadsTheMarkerOnce()
    {
        (TestMemDb db, HistoryAvailability availability) = CoveredInMemory();

        bool matched = true;
        for (int i = 0; i < 10; i++) matched &= availability.IsCoveredAndRootMatches(MarkedBlock, TestItem.KeccakA);

        Assert.That(matched, Is.True);
        db.KeyWasRead(BlockKey(MarkedBlock), 1);
    }

    [Test]
    public void RootMatches_ForAnotherRootOrAboveTheWatermark_ReadsTheMarkerEveryTime()
    {
        (TestMemDb db, HistoryAvailability availability) = CoveredInMemory();
        using (IWriteBatch batch = db.StartWriteBatch())
        {
            HistoryAvailability.MarkBlock(batch, MarkedBlock + 1, TestItem.KeccakA, HistoryAvailability.FormatVersion);
        }

        Assert.That(availability.IsCovered(MarkedBlock), Is.True, "precondition: the watermark is observed");
        for (int i = 0; i < 3; i++)
        {
            Assert.That(availability.RootMatches(MarkedBlock, TestItem.KeccakB), Is.False);
            Assert.That(availability.RootMatches(MarkedBlock + 1, TestItem.KeccakA), Is.True);
        }

        db.KeyWasRead(BlockKey(MarkedBlock), 3);
        db.KeyWasRead(BlockKey(MarkedBlock + 1), 3);
    }

    [TestCase(false, TestName = "RootMatches_AfterTheMarkerIsRemoved_FailsOnceMatchedRootsAreForgotten")]
    [TestCase(true, TestName = "RootMatches_AfterTheMarkerIsRemoved_FailsOnceObservedRecordsAreForgotten")]
    public void RootMatches_AfterTheMarkerIsRemoved_FailsOnceForgotten(bool forgetObservedRecords)
    {
        (TestMemDb db, HistoryAvailability availability) = CoveredInMemory();
        Assert.That(availability.IsCoveredAndRootMatches(MarkedBlock, TestItem.KeccakA), Is.True, "precondition: the match is remembered");

        using (IWriteBatch batch = db.StartWriteBatch())
        {
            HistoryAvailability.UnmarkBlock(batch, MarkedBlock);
        }

        if (forgetObservedRecords) availability.ForgetObservedRecords();
        else availability.ForgetMatchedRoots();

        Assert.That(availability.RootMatches(MarkedBlock, TestItem.KeccakA), Is.False);
    }

    [Test]
    public void RootMatches_AfterTheMarkerIsOverwritten_MatchesOnlyTheNewRootOnceForgotten()
    {
        (TestMemDb db, HistoryAvailability availability) = CoveredInMemory();
        Assert.That(availability.IsCoveredAndRootMatches(MarkedBlock, TestItem.KeccakA), Is.True, "precondition: the match is remembered");

        using (IWriteBatch batch = db.StartWriteBatch())
        {
            HistoryAvailability.MarkBlock(batch, MarkedBlock, TestItem.KeccakB, HistoryAvailability.FormatVersion);
        }

        availability.ForgetMatchedRoots();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.RootMatches(MarkedBlock, TestItem.KeccakA), Is.False, "the replaced root");
            Assert.That(availability.RootMatches(MarkedBlock, TestItem.KeccakB), Is.True);
        }
    }

    [Test]
    public void RootMatches_ForAnEmptySlotsBlockAndRoot_ReadsTheMarker()
    {
        (TestMemDb db, HistoryAvailability availability) = CoveredInMemory();

        Assert.That(availability.RootMatches(0, Keccak.Zero), Is.False, "a slot never written must not match block 0 with a zero root");
        db.KeyWasRead(BlockKey(0), 1);
    }

    private static (TestMemDb Db, HistoryAvailability Availability) CoveredInMemory()
    {
        TestMemDb db = new();
        using (IWriteBatch batch = db.StartWriteBatch())
        {
            HistoryAvailability.MarkBlock(batch, MarkedBlock, TestItem.KeccakA, HistoryAvailability.FormatVersion);
        }

        HistoryAvailability availability = new(db);
        availability.PublishWatermark(MarkedBlock, HistoryAvailability.FormatVersion);
        return (db, availability);
    }

    private static byte[] BlockKey(ulong block)
    {
        byte[] key = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(key, block);
        return key;
    }
}
