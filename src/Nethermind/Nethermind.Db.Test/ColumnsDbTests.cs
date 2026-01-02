// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Db.Test;

public class ColumnsDbTests
{
    string DbPath => "testdb/" + TestContext.CurrentContext.Test.Name;
    private ColumnsDb<ReceiptsColumns> _db = null!;

    [SetUp]
    public void Setup()
    {
        if (Directory.Exists(DbPath))
        {
            Directory.Delete(DbPath, true);
        }

        Directory.CreateDirectory(DbPath);
        ColumnsDb<ReceiptsColumns> columnsDb = new(DbPath,
            new("Blocks", DbPath)
            {
                DeleteOnStart = true,
            },
            new DbConfig(),
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance),
            LimboLogs.Instance,
            Enum.GetValues<ReceiptsColumns>()
        );

        _db = columnsDb;
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public void SmokeTest()
    {
        IDb colA = _db.GetColumnDb(ReceiptsColumns.Blocks);
        IDb colB = _db.GetColumnDb(ReceiptsColumns.Transactions);
        IDb defaultCol = _db.GetColumnDb(ReceiptsColumns.Default);

        colA.Set(TestItem.KeccakA, TestItem.KeccakA.BytesToArray());
        colB.Set(TestItem.KeccakA, TestItem.KeccakB.BytesToArray());

        colA.Get(TestItem.KeccakA).Should().BeEquivalentTo(TestItem.KeccakA.BytesToArray());
        colB.Get(TestItem.KeccakA).Should().BeEquivalentTo(TestItem.KeccakB.BytesToArray());

        defaultCol.Get(TestItem.KeccakB).Should().BeNull();
    }

    [Test]
    [Retry(10)]
    public void SmokeTestMemtableSize()
    {
        IDb colA = _db.GetColumnDb(ReceiptsColumns.Blocks);
        IDb colB = _db.GetColumnDb(ReceiptsColumns.Transactions);

        colA.Set(TestItem.KeccakA, TestItem.KeccakA.BytesToArray());
        colB.Set(TestItem.KeccakA, TestItem.KeccakB.BytesToArray());

        Assert.That(() => _db.GatherMetric().MemtableSize, Is.EqualTo(2566224).After(1000, 10));
    }

    [Test]
    public void SmokeTestDefaultColumn()
    {
        IDb defaultCol = _db.GetColumnDb(ReceiptsColumns.Default);

        defaultCol.Get(TestItem.KeccakB).Should().BeNull();
        defaultCol.Set(TestItem.KeccakB, TestItem.KeccakC.BytesToArray());
        defaultCol.Get(TestItem.KeccakB).Should().BeEquivalentTo(TestItem.KeccakC.BytesToArray());

        _db.Get(TestItem.KeccakB).Should().BeEquivalentTo(TestItem.KeccakC.BytesToArray());
    }

    [Test]
    public void TestWriteBatch_WriteToAllColumn()
    {
        var batch = _db.StartWriteBatch();
        var colA = batch.GetColumnBatch(ReceiptsColumns.Blocks);
        var colB = batch.GetColumnBatch(ReceiptsColumns.Transactions);

        colA.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray());
        colB.Set(TestItem.KeccakA.Bytes, TestItem.KeccakB.BytesToArray());

        batch.Dispose();

        _db.GetColumnDb(ReceiptsColumns.Blocks).Get(TestItem.KeccakA).Should()
            .BeEquivalentTo(TestItem.KeccakA.BytesToArray());
        _db.GetColumnDb(ReceiptsColumns.Transactions).Get(TestItem.KeccakA).Should()
            .BeEquivalentTo(TestItem.KeccakB.BytesToArray());
    }
}
