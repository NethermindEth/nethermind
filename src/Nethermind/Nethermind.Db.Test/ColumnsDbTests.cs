// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Db.Test;

public class ColumnsDbTests
{
    string DbPath => "testdb/" + TestContext.CurrentContext.Test.Name;
    private ColumnsDb<TestColumns> _db = null!;

    [SetUp]
    public void Setup()
    {
        if (Directory.Exists(DbPath))
        {
            Directory.Delete(DbPath, true);
        }

        Directory.CreateDirectory(DbPath);
        ColumnsDb<TestColumns> columnsDb = new(DbPath,
            new("blocks", DbPath)
            {
                BlockCacheSize = (ulong)1.KiB(),
                CacheIndexAndFilterBlocks = false,
                DeleteOnStart = true,
                WriteBufferNumber = 4,
                WriteBufferSize = (ulong)1.KiB()
            },
            new DbConfig(),
            LimboLogs.Instance,
            Enum.GetValues<TestColumns>()
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
        IDbWithSpan colA = _db.GetColumnDb(TestColumns.ColumnA);
        IDbWithSpan colB = _db.GetColumnDb(TestColumns.ColumnB);
        IDbWithSpan defaultCol = _db.GetColumnDb(TestColumns.Default);

        colA.Set(TestItem.KeccakA, TestItem.KeccakA.BytesToArray());
        colB.Set(TestItem.KeccakA, TestItem.KeccakB.BytesToArray());

        colA.Get(TestItem.KeccakA).Should().BeEquivalentTo(TestItem.KeccakA.BytesToArray());
        colB.Get(TestItem.KeccakA).Should().BeEquivalentTo(TestItem.KeccakB.BytesToArray());

        defaultCol.Get(TestItem.KeccakB).Should().BeNull();
    }

    [Test]
    public void SmokeTestDefaultColumn()
    {
        IDbWithSpan defaultCol = _db.GetColumnDb(TestColumns.Default);

        defaultCol.Get(TestItem.KeccakB).Should().BeNull();
        defaultCol.Set(TestItem.KeccakB, TestItem.KeccakC.BytesToArray());
        defaultCol.Get(TestItem.KeccakB).Should().BeEquivalentTo(TestItem.KeccakC.BytesToArray());

        _db.Get(TestItem.KeccakB).Should().BeEquivalentTo(TestItem.KeccakC.BytesToArray());
    }

    [Test]
    public void TestWriteBatch_WriteToAllColumn()
    {
        var batch = _db.StartWriteBatch();
        var colA = batch.GetColumnBatch(TestColumns.ColumnA);
        var colB = batch.GetColumnBatch(TestColumns.ColumnB);

        colA.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray());
        colB.Set(TestItem.KeccakA.Bytes, TestItem.KeccakB.BytesToArray());

        batch.Dispose();

        _db.GetColumnDb(TestColumns.ColumnA).Get(TestItem.KeccakA).Should()
            .BeEquivalentTo(TestItem.KeccakA.BytesToArray());
        _db.GetColumnDb(TestColumns.ColumnB).Get(TestItem.KeccakA).Should()
            .BeEquivalentTo(TestItem.KeccakB.BytesToArray());
    }

    enum TestColumns
    {
        Default,
        ColumnA,
        ColumnB,
    }
}
