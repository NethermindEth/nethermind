// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
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
    public void Matches_RepeatedForACoveredBlock_ReadsTheMarkerOnceAndTheFloorNever()
    {
        (TestMemDb db, HistoryAvailability availability) = CoveredInMemory();
        availability.PublishGlobalFloor(MarkedBlock - 2);

        bool matched = true;
        for (int i = 0; i < 10; i++) matched &= availability.Matches(MarkedBlock, TestItem.KeccakA);

        Assert.That(matched, Is.True);
        db.KeyWasRead(BlockKey(MarkedBlock), 1);
        db.KeyWasRead(GlobalFloorKey, 0);
    }

    [Test]
    public void IsBelowGlobalFloor_WithNoFloorPublished_ReadsTheFloorOnce()
    {
        (TestMemDb db, HistoryAvailability availability) = CoveredInMemory();

        bool below = false;
        for (int i = 0; i < 10; i++) below |= availability.IsBelowGlobalFloor(0);

        Assert.That(below, Is.False);
        db.KeyWasRead(GlobalFloorKey, 1);
    }

    [Test]
    public void Matches_RepeatedForACoveredBlock_DoesNotAllocate()
    {
        const int Reads = 1000;
        (_, HistoryAvailability availability) = CoveredInMemory();
        availability.PublishGlobalFloor(MarkedBlock - 2);
        bool matched = availability.Matches(MarkedBlock, TestItem.KeccakA);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Reads; i++) matched &= availability.Matches(MarkedBlock, TestItem.KeccakA);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(matched, Is.True);
            Assert.That(allocated, Is.Zero);
        }
    }

    [TestCase(false, TestName = "Matches_AfterTheFloorIsRaised_RefusesTheBlockBelowIt")]
    [TestCase(true, TestName = "Matches_AfterTheFloorIsPublished_RefusesTheBlockBelowIt")]
    public void Matches_AfterTheFloorMovesAboveTheBlock_Refuses(bool publish)
    {
        const ulong Block = MarkedBlock - 1;
        (TestMemDb db, HistoryAvailability availability) = CoveredInMemory();
        using (IWriteBatch batch = db.StartWriteBatch())
        {
            HistoryAvailability.MarkBlock(batch, Block, TestItem.KeccakA, HistoryAvailability.FormatVersion);
        }

        Assert.That(availability.Matches(Block, TestItem.KeccakA), Is.True, "precondition: the floor and the match are remembered");

        if (publish) availability.PublishGlobalFloor(MarkedBlock);
        else Assert.That(availability.TryRaiseGlobalFloor(MarkedBlock), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.IsBelowGlobalFloor(Block), Is.True);
            Assert.That(availability.Matches(Block, TestItem.KeccakA), Is.False);
        }
    }

    [Test]
    public void IsBelowGlobalFloor_AfterTheRecordsRestart_ReadsTheFloorAgain()
    {
        (TestMemDb db, HistoryAvailability availability) = CoveredInMemory();
        availability.PublishGlobalFloor(MarkedBlock);
        Assert.That(availability.IsBelowGlobalFloor(MarkedBlock - 1), Is.True, "precondition: the floor is remembered");

        db.Remove(GlobalFloorKey);
        availability.ForgetObservedRecords();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.IsBelowGlobalFloor(MarkedBlock - 1), Is.False, "a restart removed the floor with every other record");
            db.KeyWasRead(GlobalFloorKey, 1);
        }
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

    [Test]
    [CancelAfter(60_000)]
    public async Task Matches_UnderConcurrentFloorRaisesAndMarkerRemovals_NeverAcceptsAPrunedBlock()
    {
        const ulong Blocks = 128;
        using (IWriteBatch batch = AvailableBlocks.StartWriteBatch())
        {
            for (ulong block = 0; block < Blocks; block++)
            {
                HistoryAvailability.MarkBlock(batch, block, TestItem.KeccakA, HistoryAvailability.FormatVersion);
            }
        }

        _availability.PublishWatermark(Blocks - 1, HistoryAvailability.FormatVersion);

        // Raised only once the floor write and the marker removal have returned, so a reader that sees it knows both.
        long prunedBelow = 0;
        int violations = 0;
        using CancellationTokenSource stop = new();

        Task[] readers = new Task[Math.Max(4, Environment.ProcessorCount)];
        for (int r = 0; r < readers.Length; r++)
        {
            ulong seed = (ulong)r;
            readers[r] = Task.Run(() =>
            {
                ulong block = seed;
                while (!stop.IsCancellationRequested)
                {
                    block = (block * 6364136223846793005UL + 1442695040888963407UL) % Blocks;
                    long before = Volatile.Read(ref prunedBelow);
                    bool available = _availability.Matches(block, TestItem.KeccakA);
                    bool rootMatches = _availability.IsCoveredAndRootMatches(block, TestItem.KeccakA);
                    if (block < (ulong)before && (available || rootMatches)) Interlocked.Increment(ref violations);
                    if (block >= (ulong)Volatile.Read(ref prunedBelow) + 1 && !rootMatches) Interlocked.Increment(ref violations);
                    if (_availability.RootMatches(block, TestItem.KeccakB)) Interlocked.Increment(ref violations);
                }
            });
        }

        for (ulong floor = 1; floor < Blocks; floor++)
        {
            Assert.That(_availability.TryRaiseGlobalFloor(floor), Is.True);
            using (IWriteBatch batch = AvailableBlocks.StartWriteBatch())
            {
                HistoryAvailability.UnmarkBlock(batch, floor - 1);
            }

            _availability.ForgetMatchedRoots();
            Volatile.Write(ref prunedBelow, (long)floor);
            await Task.Delay(1);
        }

        await stop.CancelAsync();
        await Task.WhenAll(readers);

        Assert.That(violations, Is.Zero);
    }

    private static byte[] GlobalFloorKey => "history:floor:global"u8.ToArray();

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
