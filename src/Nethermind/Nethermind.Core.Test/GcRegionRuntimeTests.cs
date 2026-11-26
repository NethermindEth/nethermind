// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Memory;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class GcRegionRuntimeTests
{
    private const long GiB = 1024L * 1024 * 1024;

    // GC.GetGCMemoryInfo() in a container started with --memory=4g, server GC, .NET 10.0.13, 2 GiB held:
    // MemoryLoadBytes 2 GiB, HighMemoryLoadThresholdBytes 90% of 4 GiB, TotalAvailableMemoryBytes the implicit 3 GiB hard limit.
    [TestCase(2 * GiB, 3_865_470_566L, 90L, 4 * GiB, 50, TestName = "Container limit with the implicit 75% heap hard limit")]
    [TestCase(16 * GiB, 61_847_529_062L, 90L, 64 * GiB, 25, TestName = "Physical memory with an explicit heap hard limit")]
    [TestCase(10 * GiB, 130_567_005_798L, 95L, 128 * GiB, 8, TestName = "Physical memory with the high memory percent of a large machine")]
    public void Load_is_a_share_of_physical_memory_or_the_container_limit(long memoryLoadBytes, long highMemoryLoadThresholdBytes, long highMemoryPercent, long expectedTotal, int expectedPercent)
    {
        GcMemoryLoad load = GcMemoryLoad.FromRecorded(memoryLoadBytes, highMemoryLoadThresholdBytes,
            new Dictionary<string, object> { ["GCHighMemPercent"] = highMemoryPercent, ["GCHeapHardLimit"] = 3 * GiB });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(load.MemoryLoadBytes, Is.EqualTo(memoryLoadBytes));
            Assert.That(load.TotalMemoryBytes, Is.EqualTo(expectedTotal).Within(1));
            Assert.That((int)Math.Round(load.MemoryLoadBytes * 100.0 / load.TotalMemoryBytes), Is.EqualTo(expectedPercent));
        }
    }

    [TestCase(0L, 90L, TestName = "No high memory load threshold")]
    [TestCase(3_865_470_566L, 0L, TestName = "No high memory percent")]
    [TestCase(3_865_470_566L, null, TestName = "High memory percent not reported")]
    public void Unknown_total_is_zero(long highMemoryLoadThresholdBytes, long? highMemoryPercent)
    {
        Dictionary<string, object> configuration = [];
        if (highMemoryPercent is long percent) configuration["GCHighMemPercent"] = percent;

        Assert.That(GcMemoryLoad.FromRecorded(GiB, highMemoryLoadThresholdBytes, configuration).TotalMemoryBytes, Is.Zero);
    }

    [Test]
    public void Runtime_load_is_measured_against_physical_memory_or_the_container_limit()
    {
        System.GC.Collect();
        GcMemoryLoad load = GcRegionRuntime.Instance.GetMemoryLoad();
        GCMemoryInfo info = System.GC.GetGCMemoryInfo();
        long heapHardLimit = System.GC.GetConfigurationVariables().TryGetValue("GCHeapHardLimit", out object? value) && value is long limit ? limit : 0;

        // Without a heap hard limit the runtime reports the same memory; with one, only a container limit tells what it should be.
        long expected = heapHardLimit == 0 ? info.TotalAvailableMemoryBytes : ReadContainerMemoryLimit();
        Assume.That(expected, Is.Positive, "a heap hard limit outside a container with a cgroup v2 memory limit");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(load.TotalMemoryBytes, Is.EqualTo(expected).Within(expected / 1000), "within 0.1%");
            Assert.That(load.MemoryLoadBytes, Is.InRange(0L, load.TotalMemoryBytes));
        }
    }

    private static long ReadContainerMemoryLimit()
    {
        const string memoryMax = "/sys/fs/cgroup/memory.max";
        if (!OperatingSystem.IsLinux() || !File.Exists(memoryMax) || !long.TryParse(File.ReadAllText(memoryMax).Trim(), out long bytes)) return 0;

        // The runtime caps a container limit at physical memory (cgroup.cpp: GetRestrictedPhysicalMemoryLimit).
        foreach (string line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                return Math.Min(bytes, long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024);
            }
        }
        return bytes;
    }
}
