// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
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
    private TrackingDisposableStack _disposeStack = null!;
    private IRocksDbConfig _baseConfig = null!;

    [SetUp]
    public void SetUp()
    {
        _baseFactory = Substitute.For<IRocksDbConfigFactory>();
        _flatDbConfig = Substitute.For<IFlatDbConfig>();
        _disposeStack = new TrackingDisposableStack();
        _baseConfig = Substitute.For<IRocksDbConfig>();

        _baseConfig.RocksDbOptions.Returns("base_options=true;");
        _baseConfig.WriteBufferSize.Returns((ulong)64_000_000);

        _baseFactory.GetForDatabase(Arg.Any<string>(), Arg.Any<string>()).Returns(_baseConfig);
    }

    [TearDown]
    public void TearDown() => _disposeStack.Dispose();

    [Test]
    public void NonFlatDatabase_ReturnsBaseConfig()
    {
        _flatDbConfig.Layout.Returns(FlatLayout.Flat);
        _flatDbConfig.BlockCacheSizeBudget.Returns(1_000_000_000L);

        FlatRocksDbConfigAdjuster adjuster = new(_baseFactory, _flatDbConfig, _disposeStack, LimboLogs.Instance);

        IRocksDbConfig result = adjuster.GetForDatabase("State0", null);

        Assert.That(result, Is.SameAs(_baseConfig));
    }

    [Test]
    public void FlatDatabase_WithFlatLayout_DoesNotAddPartitionedIndexOptions()
    {
        _flatDbConfig.Layout.Returns(FlatLayout.Flat);
        _flatDbConfig.BlockCacheSizeBudget.Returns(1_000_000_000L);

        FlatRocksDbConfigAdjuster adjuster = new(_baseFactory, _flatDbConfig, _disposeStack, LimboLogs.Instance);

        IRocksDbConfig result = adjuster.GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.Metadata));

        Assert.That(result.RocksDbOptions, Does.Not.Contain("optimize_filters_for_hits"));
        Assert.That(result.RocksDbOptions, Does.Not.Contain("partition_filters"));
        Assert.That(result.RocksDbOptions, Does.Not.Contain("kTwoLevelIndexSearch"));
        Assert.That(result.BlockCache, Is.Null);
        Assert.That(_disposeStack.Count, Is.Zero);
    }

    [Test]
    public void FlatDatabase_WithFlatInTrieLayout_AddsPartitionedIndexOptions()
    {
        _flatDbConfig.Layout.Returns(FlatLayout.FlatInTrie);
        _flatDbConfig.BlockCacheSizeBudget.Returns(1_000_000_000L);

        FlatRocksDbConfigAdjuster adjuster = new(_baseFactory, _flatDbConfig, _disposeStack, LimboLogs.Instance);

        IRocksDbConfig result = adjuster.GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.Metadata));

        Assert.That(result.RocksDbOptions, Does.Contain("optimize_filters_for_hits=true;"));
        Assert.That(result.RocksDbOptions, Does.Contain("block_based_table_factory.partition_filters=true;"));
        Assert.That(result.RocksDbOptions, Does.Contain("block_based_table_factory.index_type=kTwoLevelIndexSearch;"));
    }

    [Test]
    public void FlatDatabase_DelegatesToBaseFactoryWithCorrectParameters()
    {
        _flatDbConfig.Layout.Returns(FlatLayout.Flat);
        _flatDbConfig.BlockCacheSizeBudget.Returns(1_000_000_000L);

        FlatRocksDbConfigAdjuster adjuster = new(_baseFactory, _flatDbConfig, _disposeStack, LimboLogs.Instance);

        adjuster.GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.Account));

        _baseFactory.Received(1).GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.Account));
    }

    [TestCase(nameof(FlatDbColumns.Account), 161_061_273L)]
    [TestCase(nameof(FlatDbColumns.Storage), 268_435_456L)]
    [TestCase(nameof(FlatDbColumns.StateTopNodes), 53_687_091L)]
    [TestCase(nameof(FlatDbColumns.StateNodes), 161_061_273L)]
    [TestCase(nameof(FlatDbColumns.StorageNodes), 268_435_456L)]
    [TestCase(nameof(FlatDbColumns.FallbackNodes), 161_061_273L)]
    public void FlatDatabase_AssignsBlockCacheBudgetToHotColumns(string columnName, long expectedCapacity)
        => Assert.That(FlatRocksDbConfigAdjuster.GetColumnBlockCacheCapacity(1.GiB, columnName), Is.EqualTo((ulong)expectedCapacity));

    [Test]
    public void FlatDatabase_ConfiguresBlockCachesForHotColumns()
    {
        _flatDbConfig.Layout.Returns(FlatLayout.Flat);
        _flatDbConfig.BlockCacheSizeBudget.Returns(1.GiB);

        FlatRocksDbConfigAdjuster adjuster = new(_baseFactory, _flatDbConfig, _disposeStack, LimboLogs.Instance);

        IRocksDbConfig account = adjuster.GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.Account));
        IRocksDbConfig storage = adjuster.GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.Storage));
        IRocksDbConfig stateTopNodes = adjuster.GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.StateTopNodes));
        IRocksDbConfig stateNodes = adjuster.GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.StateNodes));
        IRocksDbConfig storageNodes = adjuster.GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.StorageNodes));
        IRocksDbConfig fallbackNodes = adjuster.GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.FallbackNodes));

        Assert.That(account.BlockCache, Is.Not.Null);
        Assert.That(storage.BlockCache, Is.Not.Null);
        Assert.That(stateTopNodes.BlockCache, Is.Not.Null);
        Assert.That(stateNodes.BlockCache, Is.Not.Null);
        Assert.That(storageNodes.BlockCache, Is.Not.Null);
        Assert.That(fallbackNodes.BlockCache, Is.Not.Null);
        Assert.That(storage.BlockCache, Is.Not.EqualTo(account.BlockCache));
        Assert.That(stateTopNodes.BlockCache, Is.Not.EqualTo(account.BlockCache));
        Assert.That(stateNodes.BlockCache, Is.Not.EqualTo(account.BlockCache));
        Assert.That(storageNodes.BlockCache, Is.Not.EqualTo(account.BlockCache));
        Assert.That(fallbackNodes.BlockCache, Is.Not.EqualTo(account.BlockCache));
        Assert.That(storageNodes.RocksDbOptions, Does.Not.Contain("block_based_table_factory.block_cache=268435456;"));
        Assert.That(_disposeStack.Count, Is.EqualTo(6));
    }

    [Test]
    public void FlatDatabase_PreservesConfiguredBlockCache()
    {
        _flatDbConfig.Layout.Returns(FlatLayout.Flat);
        _flatDbConfig.BlockCacheSizeBudget.Returns(1.GiB);
        _baseConfig.RocksDbOptions.Returns("base_options=true;block_based_table_factory.block_cache=123;");

        FlatRocksDbConfigAdjuster adjuster = new(_baseFactory, _flatDbConfig, _disposeStack, LimboLogs.Instance);

        IRocksDbConfig account = adjuster.GetForDatabase(nameof(DbNames.Flat), nameof(FlatDbColumns.Account));

        Assert.That(account.BlockCache, Is.Null);
        Assert.That(account.RocksDbOptions, Does.Contain("block_based_table_factory.block_cache=123;"));
        Assert.That(account.RocksDbOptions, Does.Not.Contain("block_based_table_factory.block_cache=161061273;"));
        Assert.That(_disposeStack.Count, Is.Zero);
    }

    private sealed class TrackingDisposableStack : IDisposableStack, IDisposable
    {
        private readonly List<IDisposable> _items = [];

        public int Count => _items.Count;

        public void Push(IAsyncDisposable item) => throw new NotSupportedException();

        public void Push(IDisposable item) => _items.Add(item);

        public void Dispose()
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                _items[i].Dispose();
            }
        }
    }
}
