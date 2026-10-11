// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Exceptions;
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
    private static PbtRocksDbConfigAdjuster CreateAdjuster(IRocksDbConfigFactory baseFactory, IDisposableStack? disposeStack = null, ulong blockCacheSizeBudget = 1_000_000_000) =>
        new(baseFactory, new DbConfig { RocksDbOptions = "global=1;" }, MarkedConfig(blockCacheSizeBudget),
            disposeStack ?? Substitute.For<IDisposableStack>(), LimboLogs.Instance);

    /// <summary>Marks every option string with the column it belongs to, so the mapping is what is asserted, not the tuning.</summary>
    private static PbtConfig MarkedConfig(ulong blockCacheSizeBudget) => new()
    {
        BlockCacheSizeBudget = blockCacheSizeBudget,
        RocksDbOptions = "shared=1;",
        MetadataRocksDbOptions = $"column={nameof(PbtColumns.Metadata)};",
        AccountsRocksDbOptions = $"column={nameof(PbtColumns.Accounts)};",
        CodesRocksDbOptions = $"column={nameof(PbtColumns.Codes)};",
        CodeLeavesRocksDbOptions = $"column={nameof(PbtColumns.CodeLeaves)};",
        StoragesRocksDbOptions = $"column={nameof(PbtColumns.Storages)};",
        NodeGroupsRocksDbOptions = "column=NodeGroups;",
    };

    [TestCase(nameof(PbtColumns.Metadata), "global=1;shared=1;column=Metadata;")]
    [TestCase(nameof(PbtColumns.Accounts), "global=1;shared=1;column=Accounts;")]
    [TestCase(nameof(PbtColumns.Codes), "global=1;shared=1;column=Codes;")]
    [TestCase(nameof(PbtColumns.CodeLeaves), "global=1;shared=1;column=CodeLeaves;")]
    [TestCase(nameof(PbtColumns.Storages), "global=1;shared=1;column=Storages;")]
    [TestCase(nameof(PbtColumns.AccountNodeGroups), "global=1;shared=1;column=NodeGroups;")]
    [TestCase(nameof(PbtColumns.CodeNodeGroups), "global=1;shared=1;column=NodeGroups;")]
    [TestCase(nameof(PbtColumns.StorageNodeGroups), "global=1;shared=1;column=NodeGroups;")]
    [TestCase(nameof(PbtColumns.TopNodeGroups), "global=1;shared=1;column=NodeGroups;")]
    [TestCase(null, "global=1;shared=1;", TestName = "DatabaseItselfGetsTheSharedOptionsOnly")]
    [TestCase("NodeGroups", "global=1;shared=1;", TestName = "LegacyNodeGroupsColumnGetsTheSharedOptionsOnly")]
    public void ColumnGetsTheGlobalThenSharedThenItsOwnOptions(string? columnName, string expectedOptions)
    {
        IRocksDbConfig config = CreateAdjuster(Substitute.For<IRocksDbConfigFactory>())
            .GetForDatabase(nameof(DbNames.Pbt), columnName);

        Assert.That(config.RocksDbOptions, Is.EqualTo(expectedOptions));
    }

    /// <summary>Mirrors the flat account/storage split: the record columns get their own caches, everything else stays in the shared one.</summary>
    [Test]
    public void OnlyTheAccountAndStorageColumnsGetADedicatedBlockCache(
        [Values(null, nameof(PbtColumns.Metadata), nameof(PbtColumns.Accounts), nameof(PbtColumns.Codes), nameof(PbtColumns.Storages),
            nameof(PbtColumns.AccountNodeGroups), nameof(PbtColumns.CodeNodeGroups), nameof(PbtColumns.StorageNodeGroups), nameof(PbtColumns.TopNodeGroups))] string? columnName)
    {
        IDisposableStack disposeStack = Substitute.For<IDisposableStack>();

        IRocksDbConfig config = CreateAdjuster(Substitute.For<IRocksDbConfigFactory>(), disposeStack)
            .GetForDatabase(nameof(DbNames.Pbt), columnName);

        bool dedicated = columnName is nameof(PbtColumns.Accounts) or nameof(PbtColumns.Storages);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.BlockCache, dedicated ? Is.Not.Null : Is.Null);
            disposeStack.Received(dedicated ? 1 : 0).Push(Arg.Any<HyperClockCacheWrapper>());
        }
    }

    [Test]
    public void AccountAndStorageColumnsGetDistinctBlockCaches()
    {
        PbtRocksDbConfigAdjuster adjuster = CreateAdjuster(Substitute.For<IRocksDbConfigFactory>());

        nint? accounts = adjuster.GetForDatabase(nameof(DbNames.Pbt), nameof(PbtColumns.Accounts)).BlockCache;
        nint? storages = adjuster.GetForDatabase(nameof(DbNames.Pbt), nameof(PbtColumns.Storages)).BlockCache;

        Assert.That(accounts, Is.Not.EqualTo(storages));
    }

    [TestCase(0UL, TestName = "ZeroBlockCacheBudgetReportsConfigurationError")]
    [TestCase(2UL, TestName = "BlockCacheBudgetTooSmallToSplitReportsConfigurationError")]
    public void UnusableBlockCacheBudgetReportsConfigurationError(ulong budget)
    {
        PbtRocksDbConfigAdjuster adjuster = CreateAdjuster(Substitute.For<IRocksDbConfigFactory>(), blockCacheSizeBudget: budget);

        Assert.That(() => adjuster.GetForDatabase(nameof(DbNames.Pbt), nameof(PbtColumns.Accounts)),
            Throws.TypeOf<InvalidConfigurationException>());
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
