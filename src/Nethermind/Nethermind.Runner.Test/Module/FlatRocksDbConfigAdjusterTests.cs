// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.State.Flat;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class FlatRocksDbConfigAdjusterTests
{
    private IRocksDbConfigFactory _baseFactory = null!;
    private IFlatDbConfig _flatDbConfig = null!;
    private IDisposableStack _disposeStack = null!;
    private IAdaptiveCacheManager _adaptiveCacheManager = null!;
    private IRocksDbConfig _baseConfig = null!;

    [SetUp]
    public void SetUp()
    {
        _baseFactory = Substitute.For<IRocksDbConfigFactory>();
        _flatDbConfig = Substitute.For<IFlatDbConfig>();
        _disposeStack = Substitute.For<IDisposableStack>();
        _adaptiveCacheManager = Substitute.For<IAdaptiveCacheManager>();
        _baseConfig = Substitute.For<IRocksDbConfig>();

        _baseConfig.RocksDbOptions.Returns("base_options=true;");
        _baseConfig.WriteBufferSize.Returns((ulong)64_000_000);

        _baseFactory.GetForDatabase(Arg.Any<string>(), Arg.Any<string>()).Returns(_baseConfig);
    }

    [Test]
    public void NonFlatDatabase_ReturnsBaseConfig()
    {
        _flatDbConfig.Layout.Returns(FlatLayout.Flat);
        _flatDbConfig.BlockCacheSizeBudget.Returns(1_000_000_000UL);

        FlatRocksDbConfigAdjuster adjuster = CreateAdjuster();

        IRocksDbConfig result = adjuster.GetForDatabase("State0", null);

        Assert.That(result, Is.SameAs(_baseConfig));
    }

    [Test]
    public void FlatDatabase_WithFlatLayout_DoesNotAddPartitionedIndexOptions()
    {
        _flatDbConfig.Layout.Returns(FlatLayout.Flat);
        _flatDbConfig.BlockCacheSizeBudget.Returns(1_000_000_000UL);

        FlatRocksDbConfigAdjuster adjuster = CreateAdjuster();

        IRocksDbConfig result = adjuster.GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.Metadata));

        Assert.That(result.RocksDbOptions, Does.Not.Contain("optimize_filters_for_hits"));
        Assert.That(result.RocksDbOptions, Does.Not.Contain("partition_filters"));
        Assert.That(result.RocksDbOptions, Does.Not.Contain("kTwoLevelIndexSearch"));
    }

    [Test]
    public void FlatDatabase_WithFlatInTrieLayout_AddsPartitionedIndexOptions()
    {
        _flatDbConfig.Layout.Returns(FlatLayout.FlatInTrie);
        _flatDbConfig.BlockCacheSizeBudget.Returns(1_000_000_000UL);

        FlatRocksDbConfigAdjuster adjuster = CreateAdjuster();

        IRocksDbConfig result = adjuster.GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.Metadata));

        Assert.That(result.RocksDbOptions, Does.Contain("optimize_filters_for_hits=true;"));
        Assert.That(result.RocksDbOptions, Does.Contain("block_based_table_factory.partition_filters=true;"));
        Assert.That(result.RocksDbOptions, Does.Contain("block_based_table_factory.index_type=kTwoLevelIndexSearch;"));
    }

    [Test]
    public void FlatDatabase_DelegatesToBaseFactoryWithCorrectParameters()
    {
        _flatDbConfig.Layout.Returns(FlatLayout.Flat);
        _flatDbConfig.BlockCacheSizeBudget.Returns(1_000_000_000UL);

        FlatRocksDbConfigAdjuster adjuster = CreateAdjuster();

        adjuster.GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.Account));

        _baseFactory.Received(1).GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.Account));
        _adaptiveCacheManager.Received(1).Register(Arg.Any<IAdaptiveCache>());
    }

    private FlatRocksDbConfigAdjuster CreateAdjuster() => new(
        _baseFactory,
        _flatDbConfig,
        _adaptiveCacheManager,
        _disposeStack,
        LimboLogs.Instance);
}
