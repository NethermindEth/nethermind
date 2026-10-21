// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Config.Test;
using Nethermind.Core.Memory;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[Parallelizable(ParallelScope.All)]
public class CollectionsPerDecommitTests
{
    [Test]
    public void Defaults_compact_only_on_every_twenty_fifth_payload()
    {
        IMergeConfig config = new ConfigProvider().GetConfig<IMergeConfig>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.CollectionsPerDecommit, Is.EqualTo(25));
            Assert.That(config.CompactMemory, Is.EqualTo(GcCompaction.No));
        }
    }

    [Test]
    public void Defaults_decommit_only_under_memory_pressure_with_an_hourly_safety_net()
    {
        IMergeConfig config = new ConfigProvider().GetConfig<IMergeConfig>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.DecommitMemoryLoadPercent, Is.EqualTo(70));
            Assert.That(config.CollectionsPerUnconditionalDecommit, Is.EqualTo(300));
        }
    }

    [TestCase(nameof(IMergeConfig.CollectionsPerDecommit))]
    [TestCase(nameof(IMergeConfig.DecommitMemoryLoadPercent))]
    [TestCase(nameof(IMergeConfig.CollectionsPerUnconditionalDecommit))]
    public void Documented_decommit_default_matches_the_implementation(string propertyName) =>
        StandardConfigTests.CheckDefault(typeof(IMergeConfig).GetProperty(propertyName)!, new MergeConfig());

    [Test]
    public void Strategy_carries_the_decommit_policy()
    {
        ConfigProvider configProvider = new();
        configProvider.AddSource(new ArgsConfigSource(new Dictionary<string, string>
        {
            { "Merge.CollectionsPerDecommit", "10" },
            { "Merge.DecommitMemoryLoadPercent", "85" },
            { "Merge.CollectionsPerUnconditionalDecommit", "-1" }
        }));

        NoSyncGcRegionStrategy strategy = new(Substitute.For<ISyncModeSelector>(), configProvider.GetConfig<IMergeConfig>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(strategy.CollectionsPerDecommit, Is.EqualTo(10));
            Assert.That(strategy.DecommitMemoryLoadPercent, Is.EqualTo(85));
            Assert.That(strategy.CollectionsPerUnconditionalDecommit, Is.EqualTo(-1));
        }
    }

    [Test]
    public void Negative_sentinel_binds_without_overflow()
    {
        ConfigProvider configProvider = new();
        configProvider.AddSource(new ArgsConfigSource(new Dictionary<string, string>
        {
            { "Merge.CollectionsPerDecommit", "-1" }
        }));

        IMergeConfig mergeConfig = configProvider.GetConfig<IMergeConfig>();

        Assert.That(mergeConfig.CollectionsPerDecommit, Is.EqualTo(-1));
    }

    [Test]
    public void NoGCStrategy_uses_negative_sentinel_to_disable_decommit()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(NoGCStrategy.Instance.CollectionsPerDecommit, Is.EqualTo(-1));
            Assert.That(((IGCStrategy)NoGCStrategy.Instance).CollectionsPerUnconditionalDecommit, Is.EqualTo(-1));
        }
    }
}
