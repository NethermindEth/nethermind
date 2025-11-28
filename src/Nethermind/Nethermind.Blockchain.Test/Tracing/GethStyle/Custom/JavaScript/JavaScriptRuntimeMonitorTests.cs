// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Tracing.GethStyle.Custom.JavaScript;

[TestFixture]
public class JavaScriptRuntimeMonitorTests
{
    [SetUp]
    public void SetUp() => JavaScriptRuntimeMonitor.ResetForTests();

    [Test]
    public void Does_not_collect_when_usage_is_below_threshold()
    {
        JavaScriptEngineSettings settings = JavaScriptEngineSettings.Default with
        {
            GarbageCollectionThresholdBytes = 1_000,
            MinGarbageCollectionInterval = TimeSpan.Zero
        };

        bool collected = false;

        JavaScriptRuntimeMonitor.MaybeCollectGarbage(
            settings,
            () => new JavaScriptRuntimeMonitor.JavaScriptRuntimeHeapSnapshot(500, 10_000),
            _ => collected = true);

        collected.Should().BeFalse();
    }

    [Test]
    public void Collects_when_usage_crosses_threshold()
    {
        JavaScriptEngineSettings settings = JavaScriptEngineSettings.Default with
        {
            GarbageCollectionThresholdBytes = 256,
            MinGarbageCollectionInterval = TimeSpan.Zero
        };

        int collected = 0;

        JavaScriptRuntimeMonitor.MaybeCollectGarbage(
            settings,
            () => new JavaScriptRuntimeMonitor.JavaScriptRuntimeHeapSnapshot(300, 10_000),
            _ => collected++);

        collected.Should().Be(1);
    }

    [Test]
    public void Respects_min_interval_for_small_growth()
    {
        JavaScriptEngineSettings settings = JavaScriptEngineSettings.Default with
        {
            GarbageCollectionThresholdBytes = 128,
            MinGarbageCollectionInterval = TimeSpan.FromHours(1)
        };

        int collected = 0;

        JavaScriptRuntimeMonitor.MaybeCollectGarbage(
            settings,
            () => new JavaScriptRuntimeMonitor.JavaScriptRuntimeHeapSnapshot(256, 10_000),
            _ => collected++);

        JavaScriptRuntimeMonitor.MaybeCollectGarbage(
            settings,
            () => new JavaScriptRuntimeMonitor.JavaScriptRuntimeHeapSnapshot(260, 10_000),
            _ => collected++);

        collected.Should().Be(1);
    }

    [Test]
    public void Allows_collection_when_usage_spikes()
    {
        JavaScriptEngineSettings settings = JavaScriptEngineSettings.Default with
        {
            GarbageCollectionThresholdBytes = 128,
            MinGarbageCollectionInterval = TimeSpan.FromHours(1)
        };

        int collected = 0;

        JavaScriptRuntimeMonitor.MaybeCollectGarbage(
            settings,
            () => new JavaScriptRuntimeMonitor.JavaScriptRuntimeHeapSnapshot(256, 10_000),
            _ => collected++);

        JavaScriptRuntimeMonitor.MaybeCollectGarbage(
            settings,
            () => new JavaScriptRuntimeMonitor.JavaScriptRuntimeHeapSnapshot(1024, 10_000),
            _ => collected++);

        collected.Should().Be(2);
    }
}
