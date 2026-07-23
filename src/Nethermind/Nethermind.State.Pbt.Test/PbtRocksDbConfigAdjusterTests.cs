// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FastEnumUtility;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class PbtRocksDbConfigAdjusterTests
{
    private static PbtRocksDbConfigAdjuster CreateAdjuster(IRocksDbConfigFactory baseFactory) =>
        new(baseFactory, new DbConfig { RocksDbOptions = "global=1;" }, MarkedConfig());

    /// <summary>Marks every option string with the column it belongs to, so the mapping is what is asserted, not the tuning.</summary>
    private static PbtConfig MarkedConfig() => new()
    {
        RocksDbOptions = "shared=1;",
        MetadataRocksDbOptions = $"column={nameof(PbtColumns.Metadata)};",
        AccountRocksDbOptions = $"column={nameof(PbtColumns.Account)};",
        StorageRocksDbOptions = $"column={nameof(PbtColumns.Storage)};",
        AccountLeavesRocksDbOptions = $"column={nameof(PbtColumns.AccountLeaves)};",
        CodeLeavesRocksDbOptions = $"column={nameof(PbtColumns.CodeLeaves)};",
        StorageLeavesRocksDbOptions = $"column={nameof(PbtColumns.StorageLeaves)};",
        AccountTrieNodesRocksDbOptions = $"column={nameof(PbtColumns.AccountTrieNodes)};",
        CodeTrieNodesRocksDbOptions = $"column={nameof(PbtColumns.CodeTrieNodes)};",
        StorageTrieNodesRocksDbOptions = $"column={nameof(PbtColumns.StorageTrieNodes)};",
    };

    [TestCase(PbtColumns.Metadata)]
    [TestCase(PbtColumns.Account)]
    [TestCase(PbtColumns.Storage)]
    [TestCase(PbtColumns.AccountLeaves)]
    [TestCase(PbtColumns.CodeLeaves)]
    [TestCase(PbtColumns.StorageLeaves)]
    [TestCase(PbtColumns.AccountTrieNodes)]
    [TestCase(PbtColumns.CodeTrieNodes)]
    [TestCase(PbtColumns.StorageTrieNodes)]
    public void EveryColumnGetsTheGlobalThenSharedThenItsOwnOptions(PbtColumns column)
    {
        IRocksDbConfig config = CreateAdjuster(Substitute.For<IRocksDbConfigFactory>())
            .GetForDatabase(nameof(DbNames.Pbt), column.ToString());

        Assert.That(config.RocksDbOptions, Is.EqualTo($"global=1;shared=1;column={column};"));
    }

    [Test]
    public void PbtDatabaseItselfGetsTheSharedOptionsOnly()
    {
        IRocksDbConfig config = CreateAdjuster(Substitute.For<IRocksDbConfigFactory>())
            .GetForDatabase(nameof(DbNames.Pbt), null);

        Assert.That(config.RocksDbOptions, Is.EqualTo("global=1;shared=1;"));
    }

    /// <summary>An option rocksdb does not know fails the database open, which with pbt enabled is the node failing to start.</summary>
    [Test]
    public void DefaultOptionsOfEveryColumnAreAcceptedByRocksDb()
    {
        using TempPath dbPath = TempPath.GetTempDirectory();
        DbConfig dbConfig = new();
        PbtRocksDbConfigAdjuster adjuster = new(Substitute.For<IRocksDbConfigFactory>(), dbConfig, new PbtConfig());

        using ColumnsDb<PbtColumns> db = new(dbPath.Path, new DbSettings(nameof(DbNames.Pbt), DbNames.Pbt), dbConfig,
            adjuster, LimboLogs.Instance, FastEnum.GetValues<PbtColumns>());

        foreach (PbtColumns column in FastEnum.GetValues<PbtColumns>())
        {
            db.GetColumnDb(column).Set([(byte)column], [1]);
            Assert.That(db.GetColumnDb(column).Get([(byte)column]), Is.EqualTo(new byte[] { 1 }));
        }
    }

    [Test]
    public void OtherDatabasesAreLeftToTheBaseFactory()
    {
        IRocksDbConfigFactory baseFactory = Substitute.For<IRocksDbConfigFactory>();
        IRocksDbConfig baseConfig = Substitute.For<IRocksDbConfig>();
        baseFactory.GetForDatabase(nameof(DbNames.Blocks), null).Returns(baseConfig);

        IRocksDbConfig config = CreateAdjuster(baseFactory).GetForDatabase(nameof(DbNames.Blocks), null);

        Assert.That(config, Is.SameAs(baseConfig));
    }
}
