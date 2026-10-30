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
using Testably.Abstractions;

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
        DbConfig dbConfig = new();
        _columns = (ColumnsDb<FlatHistoryColumns>)new RocksDbFactory(
                new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
                dbConfig, new HyperClockCacheWrapper(), new RealFileSystem(), LimboLogs.Instance, _directory.Path)
            .CreateColumnsDb<FlatHistoryColumns>(new DbSettings(nameof(HistoryAvailabilityTests), _directory.Path));
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
}
