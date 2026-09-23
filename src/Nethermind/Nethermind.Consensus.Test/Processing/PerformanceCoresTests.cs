// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

public class PerformanceCoresTests
{
    // An i7-13700H: 6 performance cores with two hyperthreads each are CPUs 0-11, siblings 0-1, 2-3 and so on;
    // 8 efficiency cores are 12-19.
    private const string PerformanceCpus = "0-11";
    private static readonly Func<int, string> Siblings = static cpu => cpu < 12 ? $"{cpu & ~1}-{cpu | 1}" : $"{cpu}";

    [TestCase("0-11", 12, TestName = "ParseCpuList_SingleRange_ListsEveryCpu")]
    [TestCase("0-3,8,10-11\n", 7, TestName = "ParseCpuList_RangesAndSingles_ListsEach")]
    [TestCase("5", 1, TestName = "ParseCpuList_SingleCpu_ListsOne")]
    [TestCase("", 0, TestName = "ParseCpuList_Empty_ListsNone")]
    [TestCase("4-2,x,-1", 0, TestName = "ParseCpuList_Malformed_ListsNone")]
    [TestCase("0-3,1020-1100,2000", 4, TestName = "ParseCpuList_BeyondTheMask_SkipsWhatTheMaskCannotHold")]
    public void ParseCpuList_ParsesLinuxCpuLists(string cpuList, int expected) =>
        Assert.That(PerformanceCores.ParseCpuList(cpuList), Has.Count.EqualTo(expected), "the number of CPUs the list names");

    [TestCase(ProcessingCores.Performance, "0-19", new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, TestName = "TryBuildMask_Performance_UnpinnedHybrid_BothHyperthreads")]
    [TestCase(ProcessingCores.Performance, "0-3,12-19", new[] { 0, 1, 2, 3 }, TestName = "TryBuildMask_Performance_PinnedToMixedCpus_AllowedPerformanceCores")]
    [TestCase(ProcessingCores.Performance, "\t0-3,12-19\n", new[] { 0, 1, 2, 3 }, TestName = "TryBuildMask_Performance_AsProcSpellsIt_ParsesTheList")]
    [TestCase(ProcessingCores.Performance, null, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, TestName = "TryBuildMask_Performance_AllowedCpusUnknown_BothHyperthreads")]
    [TestCase(ProcessingCores.Performance, "", new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, TestName = "TryBuildMask_Performance_AllowedCpusUnparsable_TreatedAsUnknown")]
    [TestCase(ProcessingCores.PerformancePhysical, "0-19", new[] { 0, 2, 4, 6, 8, 10 }, TestName = "TryBuildMask_PerformancePhysical_UnpinnedHybrid_OneHyperthreadPerCore")]
    [TestCase(ProcessingCores.PerformancePhysical, "1,3-5,12-19", new[] { 1, 3, 4 }, TestName = "TryBuildMask_PerformancePhysical_PinnedToOddHyperthreads_KeepsTheAllowedOne")]
    [TestCase(ProcessingCores.PerformancePhysical, "0-11", new[] { 0, 2, 4, 6, 8, 10 }, TestName = "TryBuildMask_PerformancePhysical_PinnedToPerformanceCores_StillNarrowsToOnePerCore")]
    public void TryBuildMask_SelectsLogicalProcessors(ProcessingCores cores, string allowedCpus, int[] expected)
    {
        bool narrows = PerformanceCores.TryBuildMask(cores, PerformanceCpus, allowedCpus, Siblings, out PerformanceCores.CpuMask mask, out int[] cpus);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(narrows, Is.True);
            Assert.That(cpus, Is.EqualTo(expected));
            Assert.That(Enumerable.Range(0, PerformanceCores.MaxCpus).Where(mask.Contains), Is.EqualTo(expected), "the mask holds exactly the listed CPUs");
        }
    }

    [TestCase(ProcessingCores.All, PerformanceCpus, "0-19", TestName = "TryBuildMask_All_DoesNotNarrow")]
    [TestCase(ProcessingCores.Performance, null, "0-15", TestName = "TryBuildMask_NoCoreTypes_DoesNotNarrow")]
    [TestCase(ProcessingCores.Performance, PerformanceCpus, "12-19", TestName = "TryBuildMask_PinnedToEfficiencyCores_DoesNotNarrow")]
    [TestCase(ProcessingCores.Performance, PerformanceCpus, "0-11", TestName = "TryBuildMask_PinnedToPerformanceCores_HasNothingToNarrow")]
    [TestCase(ProcessingCores.PerformancePhysical, PerformanceCpus, "0,2,4", TestName = "TryBuildMask_PerformancePhysical_OneHyperthreadPerCoreAlready_HasNothingToNarrow")]
    public void TryBuildMask_DoesNotNarrow(ProcessingCores cores, string performanceCpus, string allowedCpus)
    {
        bool narrows = PerformanceCores.TryBuildMask(cores, performanceCpus, allowedCpus, Siblings, out _, out int[] cpus);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(narrows, Is.False);
            Assert.That(cpus, Is.Empty);
        }
    }
}
