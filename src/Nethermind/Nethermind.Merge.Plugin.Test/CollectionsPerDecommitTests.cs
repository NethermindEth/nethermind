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

    [TestCase(null, null, NoGcRegionMode.Guard, 0L)]
    [TestCase("Always", "64", NoGcRegionMode.Always, 64_000_000L)]
    [TestCase("Never", "0", NoGcRegionMode.Never, 0L)]
    [TestCase("Guard", "-5", NoGcRegionMode.Guard, 0L)]
    public void No_gc_region_mode_defaults_to_guard_and_binds(string? mode, string? guardMb, NoGcRegionMode expectedMode, long expectedGuardBytes)
    {
        ConfigProvider configProvider = new();
        Dictionary<string, string> args = [];
        if (mode is not null) args["Merge.NoGcRegionOnNewPayload"] = mode;
        if (guardMb is not null) args["Merge.NoGcRegionGuardMb"] = guardMb;
        configProvider.AddSource(new ArgsConfigSource(args));

        IMergeConfig mergeConfig = configProvider.GetConfig<IMergeConfig>();
        NoSyncGcRegionStrategy strategy = new(Substitute.For<ISyncModeSelector>(), mergeConfig);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mergeConfig.NoGcRegionOnNewPayload, Is.EqualTo(expectedMode));
            Assert.That(strategy.NoGCRegionMode, Is.EqualTo(expectedMode));
            Assert.That(strategy.NoGCRegionGuardBytes, Is.EqualTo(expectedGuardBytes));
            Assert.That(NoGCStrategy.Instance.NoGCRegionMode, Is.EqualTo(NoGcRegionMode.Never));
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
