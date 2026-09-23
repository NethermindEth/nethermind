// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Cpu;
using NUnit.Framework;

namespace Nethermind.Core.Test.Cpu;

public class RuntimeInformationTests
{
    /// <summary>
    /// The <c>Nethermind tests (Single Proc)</c> workflow
    /// (<c>.github/workflows/nethermind-tests-single-proc.yml</c>) pins the runtime to one processor
    /// with <c>DOTNET_PROCESSOR_COUNT</c> so the paths behind
    /// <see cref="RuntimeInformation.IsSingleProcessor"/> run. That run is only worth its minutes if
    /// the pin reaches the runtime, so it fails here if the runtime ignores it.
    /// </summary>
    [Test]
    public void Processor_count_follows_the_runtime_pin()
    {
        string? pinned = Environment.GetEnvironmentVariable("DOTNET_PROCESSOR_COUNT");
        Assume.That(pinned, Is.Not.Null, "meaningful only when DOTNET_PROCESSOR_COUNT pins the runtime");

        int expected = int.Parse(pinned!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Environment.ProcessorCount, Is.EqualTo(expected));
            Assert.That(RuntimeInformation.ProcessorCount, Is.EqualTo(expected));
            Assert.That(RuntimeInformation.IsSingleProcessor, Is.EqualTo(expected == 1));
        }
    }

    [Test]
    public void Single_processor_flag_follows_the_count()
        => Assert.That(RuntimeInformation.IsSingleProcessor, Is.EqualTo(RuntimeInformation.ProcessorCount <= 1));

    [TestCase("0-11", 12, TestName = "CountCpuList_SingleRange_CountsEveryCpu")]
    [TestCase("0-3,8,10-11\n", 7, TestName = "CountCpuList_RangesAndSingles_CountsEach")]
    [TestCase("5", 1, TestName = "CountCpuList_SingleCpu_CountsOne")]
    [TestCase("", 0, TestName = "CountCpuList_Empty_CountsNone")]
    [TestCase("4-2,x", 0, TestName = "CountCpuList_Malformed_CountsNone")]
    public void CountCpuList_ParsesLinuxCpuLists(string cpuList, int expected) =>
        Assert.That(RuntimeInformation.CountCpuList(cpuList), Is.EqualTo(expected), "the number of CPUs the list names");

    [Test]
    public void PerformanceProcessorCount_NeverExceedsTheLogicalCount() =>
        Assert.That(RuntimeInformation.PerformanceProcessorCount, Is.InRange(1, RuntimeInformation.ProcessorCount), "performance cores are a subset of the logical processors");
}
