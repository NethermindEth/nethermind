// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Config;
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

    [TestCase(null, false)]
    [TestCase("true", true)]
    [TestCase("false", false)]
    public void No_gc_region_on_new_payload_is_off_unless_configured(string? value, bool expected)
    {
        ConfigProvider configProvider = new();
        if (value is not null)
        {
            configProvider.AddSource(new ArgsConfigSource(new Dictionary<string, string>
            {
                { "Merge.EnterNoGcRegionOnNewPayload", value }
            }));
        }

        IMergeConfig mergeConfig = configProvider.GetConfig<IMergeConfig>();
        NoSyncGcRegionStrategy strategy = new(Substitute.For<ISyncModeSelector>(), mergeConfig);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mergeConfig.EnterNoGcRegionOnNewPayload, Is.EqualTo(expected));
            Assert.That(strategy.EnterNoGCRegion, Is.EqualTo(expected));
            Assert.That(NoGCStrategy.Instance.EnterNoGCRegion, Is.False);
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
    public void NoGCStrategy_uses_negative_sentinel_to_disable_decommit() =>
        Assert.That(NoGCStrategy.Instance.CollectionsPerDecommit, Is.EqualTo(-1));
}
