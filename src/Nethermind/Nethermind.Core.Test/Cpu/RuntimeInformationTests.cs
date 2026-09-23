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

    [TestCase("0-11", 12, TestName = "ParseCpuList_SingleRange_ListsEveryCpu")]
    [TestCase("0-3,8,10-11\n", 7, TestName = "ParseCpuList_RangesAndSingles_ListsEach")]
    [TestCase("5", 1, TestName = "ParseCpuList_SingleCpu_ListsOne")]
    [TestCase("", 0, TestName = "ParseCpuList_Empty_ListsNone")]
    [TestCase("4-2,x,-1", 0, TestName = "ParseCpuList_Malformed_ListsNone")]
    public void ParseCpuList_ParsesLinuxCpuLists(string cpuList, int expected) =>
        Assert.That(RuntimeInformation.ParseCpuList(cpuList), Has.Count.EqualTo(expected), "the number of CPUs the list names");

    [TestCase("0-11", "0-19", 20, 12, TestName = "PerformanceCountFrom_UnpinnedHybrid_CountsPerformanceCores")]
    [TestCase("0-11", "0-15", 16, 12, TestName = "PerformanceCountFrom_PinnedToMostlyPerformanceCpus_CountsPerformanceCores")]
    [TestCase("0-11", "0-3,12-19", 12, 8, TestName = "PerformanceCountFrom_PinnedToMostlyEfficiencyCpus_CountsEfficiencyCores")]
    [TestCase("0-11", "0,12-19", 9, 8, TestName = "PerformanceCountFrom_PinnedToOnePerformanceCpu_KeepsEfficiencyCores")]
    [TestCase("0-11\n", "\t0-3,12-19\n", 12, 8, TestName = "PerformanceCountFrom_AsTheProcFilesSpellThem_ParsesBothLists")]
    [TestCase("0-11", "12-19", 8, 8, TestName = "PerformanceCountFrom_PinnedToEfficiencyCores_CountsEfficiencyCores")]
    [TestCase(null, "0-15", 16, 16, TestName = "PerformanceCountFrom_NoCoreTypes_FallsBackToLogicalCount")]
    [TestCase("0-11", null, 20, 12, TestName = "PerformanceCountFrom_AllowedCpusUnknown_CountsPerformanceCores")]
    [TestCase("0-11", "", 20, 20, TestName = "PerformanceCountFrom_AllowedCpusUnparsable_FallsBackToLogicalCount")]
    [TestCase("0-11", "0-19", 1, 1, TestName = "PerformanceCountFrom_FewerLogicalProcessors_ClampsToLogicalCount")]
    public void PerformanceCountFrom_CountsLargerKindOfCore(string? performanceCpus, string? allowedCpus, int processorCount, int expected) =>
        Assert.That(RuntimeInformation.PerformanceCountFrom(performanceCpus, allowedCpus, processorCount), Is.EqualTo(expected),
            "the larger kind of core the process may run on, never more than its logical processors");
}
