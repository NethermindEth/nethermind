// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

public class PerformanceCoresTests
{
    [TestCase("0-11", 12, TestName = "ParseCpuList_SingleRange_ListsEveryCpu")]
    [TestCase("0-3,8,10-11\n", 7, TestName = "ParseCpuList_RangesAndSingles_ListsEach")]
    [TestCase("5", 1, TestName = "ParseCpuList_SingleCpu_ListsOne")]
    [TestCase("", 0, TestName = "ParseCpuList_Empty_ListsNone")]
    [TestCase("4-2,x,-1", 0, TestName = "ParseCpuList_Malformed_ListsNone")]
    [TestCase("0-3,1020-1100,2000", 4, TestName = "ParseCpuList_BeyondTheMask_SkipsWhatTheMaskCannotHold")]
    public void ParseCpuList_ParsesLinuxCpuLists(string cpuList, int expected) =>
        Assert.That(PerformanceCores.ParseCpuList(cpuList), Has.Count.EqualTo(expected), "the number of CPUs the list names");

    // An i7-13700H: 6 performance cores with SMT are CPUs 0-11, 8 efficiency cores are 12-19.
    [TestCase("0-11", "0-19", new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, TestName = "TryBuildMask_UnpinnedHybrid_NarrowsToPerformanceCores")]
    [TestCase("0-11", "0-3,12-19", new[] { 0, 1, 2, 3 }, TestName = "TryBuildMask_PinnedToMixedCpus_NarrowsToAllowedPerformanceCores")]
    [TestCase("0-11\n", "\t0-3,12-19\n", new[] { 0, 1, 2, 3 }, TestName = "TryBuildMask_AsTheProcFilesSpellThem_ParsesBothLists")]
    [TestCase("0-11", null, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, TestName = "TryBuildMask_AllowedCpusUnknown_NarrowsToPerformanceCores")]
    [TestCase("0-11", "", new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, TestName = "TryBuildMask_AllowedCpusUnparsable_TreatedAsUnknown")]
    public void TryBuildMask_NarrowsToAllowedPerformanceCores(string performanceCpus, string? allowedCpus, int[] expected)
    {
        bool narrows = PerformanceCores.TryBuildMask(performanceCpus, allowedCpus, out PerformanceCores.CpuMask mask, out int[] cpus);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(narrows, Is.True);
            Assert.That(cpus, Is.EqualTo(expected));
            Assert.That(Enumerable.Range(0, PerformanceCores.MaxCpus).Where(mask.Contains), Is.EqualTo(expected), "the mask holds exactly the listed CPUs");
        }
    }

    [TestCase(null, "0-15", TestName = "TryBuildMask_NoCoreTypes_DoesNotNarrow")]
    [TestCase("0-11", "12-19", TestName = "TryBuildMask_PinnedToEfficiencyCores_DoesNotNarrow")]
    [TestCase("0-11", "0-11", TestName = "TryBuildMask_PinnedToPerformanceCores_HasNothingToNarrow")]
    [TestCase("0-11", "2-5", TestName = "TryBuildMask_PinnedInsidePerformanceCores_HasNothingToNarrow")]
    public void TryBuildMask_DoesNotNarrow(string? performanceCpus, string allowedCpus)
    {
        bool narrows = PerformanceCores.TryBuildMask(performanceCpus, allowedCpus, out _, out int[] cpus);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(narrows, Is.False);
            Assert.That(cpus, Is.Empty);
        }
    }
}
