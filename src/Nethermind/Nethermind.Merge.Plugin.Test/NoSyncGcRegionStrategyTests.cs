// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
public class NoSyncGcRegionStrategyTests
{
    private static NoSyncGcRegionStrategy CreateStrategy(Dictionary<string, string> args)
    {
        ConfigProvider configProvider = new();
        configProvider.AddSource(new ArgsConfigSource(args));
        return new NoSyncGcRegionStrategy(Substitute.For<ISyncModeSelector>(), configProvider.GetConfig<IMergeConfig>());
    }

    [TestCase(12, 1_500)]
    [TestCase(2, 250)]
    public void Default_post_block_delay_follows_the_slot_and_adapts(int secondsPerSlot, int expectedDelayMs)
    {
        NoSyncGcRegionStrategy strategy = CreateStrategy(new() { { "Merge.SecondsPerSlot", secondsPerSlot.ToString() } });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(strategy.PostBlockDelayMs, Is.EqualTo(expectedDelayMs));
            Assert.That(strategy.AdaptivePostBlockDelay, Is.True);
        }
    }

    [TestCase(0)]
    [TestCase(400)]
    public void Explicit_post_block_delay_is_fixed(int delayMs)
    {
        NoSyncGcRegionStrategy strategy = CreateStrategy(new() { { "Merge.PostBlockGcDelayMs", delayMs.ToString() } });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(strategy.PostBlockDelayMs, Is.EqualTo(delayMs));
            Assert.That(strategy.AdaptivePostBlockDelay, Is.False);
        }
    }

    [Test]
    public void No_gc_region_mode_defaults_to_always_and_reaches_the_strategy()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CreateStrategy([]).NoGcRegionOnNewPayload, Is.EqualTo(NoGcRegionMode.Always));
            Assert.That(CreateStrategy(new() { { "Merge.NoGcRegionOnNewPayload", "Never" } }).NoGcRegionOnNewPayload, Is.EqualTo(NoGcRegionMode.Never));
        }
    }

    [Test]
    public void Documented_no_gc_region_default_matches_the_implementation() =>
        StandardConfigTests.CheckDefault(typeof(IMergeConfig).GetProperty(nameof(IMergeConfig.NoGcRegionOnNewPayload))!, new MergeConfig());
}
