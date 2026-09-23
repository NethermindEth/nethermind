// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

public class PerformanceCoresTests
{
    // An i7-13700H: 6 performance cores with two hyperthreads each are CPUs 0-11, siblings 0-1, 2-3 and so on;
    // 8 efficiency cores are 12-19.
    private const string PerformanceCpus = "0-11";
    private static readonly Func<int, string> Siblings = static cpu => cpu < 12 ? $"{cpu & ~1}-{cpu | 1}" : $"{cpu}";

    private static HashSet<int> Allowed(string allowedCpus) => allowedCpus is null ? null : PerformanceCores.ParseCpuList(allowedCpus);

    [TestCase("0-11", 12, TestName = "ParseCpuList_SingleRange_ListsEveryCpu")]
    [TestCase("0-3,8,10-11\n", 7, TestName = "ParseCpuList_RangesAndSingles_ListsEach")]
    [TestCase("5", 1, TestName = "ParseCpuList_SingleCpu_ListsOne")]
    [TestCase("", 0, TestName = "ParseCpuList_Empty_ListsNone")]
    [TestCase("4-2,x,-1", 0, TestName = "ParseCpuList_Malformed_ListsNone")]
    [TestCase("0-3,1020-1100,2000", 8, TestName = "ParseCpuList_BeyondTheMask_KeepsWhatTheMaskHolds")]
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
        bool narrows = PerformanceCores.TryBuildMask(cores, PerformanceCpus, Allowed(allowedCpus), Siblings, out PerformanceCores.CpuMask mask, out int[] cpus);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(narrows, Is.True);
            Assert.That(cpus, Is.EqualTo(expected));
            Assert.That(Enumerable.Range(0, PerformanceCores.MaxCpus).Where(mask.Contains), Is.EqualTo(expected), "the mask holds exactly the listed CPUs");
        }
    }

    [Test]
    public void Undefined_mode_narrows_nothing()
    {
        ProcessingCores undefined = (ProcessingCores)9;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PerformanceCores.Cpus(undefined).ToArray(), Is.Empty, "a number the config binder accepts must not index past the modes");
            Assert.DoesNotThrow(() => PerformanceCores.NarrowCurrentThread(undefined, LimboLogs.Instance.GetClassLogger<PerformanceCoresTests>()).Dispose());
        }
    }

    [TestCase(ProcessingCores.All, PerformanceCpus, "0-19", TestName = "TryBuildMask_All_DoesNotNarrow")]
    [TestCase(ProcessingCores.Performance, null, "0-15", TestName = "TryBuildMask_NoCoreTypes_DoesNotNarrow")]
    [TestCase(ProcessingCores.Performance, PerformanceCpus, "12-19", TestName = "TryBuildMask_PinnedToEfficiencyCores_DoesNotNarrow")]
    [TestCase(ProcessingCores.Performance, PerformanceCpus, "0-11", TestName = "TryBuildMask_PinnedToPerformanceCores_HasNothingToNarrow")]
    [TestCase(ProcessingCores.PerformancePhysical, PerformanceCpus, "0,2,4", TestName = "TryBuildMask_PerformancePhysical_OneHyperthreadPerCoreAlready_HasNothingToNarrow")]
    public void TryBuildMask_DoesNotNarrow(ProcessingCores cores, string performanceCpus, string allowedCpus)
    {
        bool narrows = PerformanceCores.TryBuildMask(cores, performanceCpus, Allowed(allowedCpus), Siblings, out _, out int[] cpus);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(narrows, Is.False);
            Assert.That(cpus, Is.Empty);
        }
    }

    [Test]
    public void TryBuildMask_PerformancePhysical_SiblingsUnknown_DoesNotNarrow()
    {
        bool narrows = PerformanceCores.TryBuildMask(ProcessingCores.PerformancePhysical, PerformanceCpus, Allowed("0-19"), static _ => null, out _, out int[] cpus);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(narrows, Is.False, "without the hyperthreads it would narrow to what Performance does, and the arms would measure the same thing");
            Assert.That(cpus, Is.Empty);
        }
    }

    [TestCase(PerformanceCpus, "0-19", new[] { 12, 13, 14, 15, 16, 17, 18, 19 }, TestName = "TryBuildEfficiencyMask_UnpinnedHybrid_TheEfficiencyCores")]
    [TestCase(PerformanceCpus, "4-15", new[] { 12, 13, 14, 15 }, TestName = "TryBuildEfficiencyMask_MixedCpuset_TheAllowedEfficiencyCores")]
    public void TryBuildEfficiencyMask_SelectsTheOtherCores(string performanceCpus, string allowedCpus, int[] expected)
    {
        bool built = PerformanceCores.TryBuildEfficiencyMask(performanceCpus, Allowed(allowedCpus), out PerformanceCores.CpuMask mask, out int[] cpus);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built, Is.True);
            Assert.That(cpus, Is.EqualTo(expected));
            Assert.That(Enumerable.Range(0, PerformanceCores.MaxCpus).Where(mask.Contains), Is.EqualTo(expected));
        }
    }

    [TestCase(PerformanceCpus, "0-11", TestName = "TryBuildEfficiencyMask_PerformanceCoresOnly_NoSplit")]
    [TestCase(PerformanceCpus, "12-19", TestName = "TryBuildEfficiencyMask_EfficiencyCoresOnly_NoSplit")]
    [TestCase(null, "0-15", TestName = "TryBuildEfficiencyMask_NoCoreTypes_NoSplit")]
    public void TryBuildEfficiencyMask_OneKindOfCore_DoesNotSplit(string performanceCpus, string allowedCpus) =>
        Assert.That(PerformanceCores.TryBuildEfficiencyMask(performanceCpus, Allowed(allowedCpus), out _, out _), Is.False);

    // Favored cores: 4-5 report the highest CPPC level, the other performance cores less, the efficiency cores least.
    private static long? FavoredCore(int cpu) => cpu is 4 or 5 ? 72 : cpu < 12 ? 68 : 40;

    [Test]
    public void TryBuildMask_Fastest_FavoredCore_BothHyperthreadsOfThatCore()
    {
        bool narrows = PerformanceCores.TryBuildMask(ProcessingCores.Fastest, PerformanceCpus, Allowed("0-19"), Siblings, out PerformanceCores.CpuMask mask, out int[] cpus, FavoredCore);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(narrows, Is.True);
            Assert.That(cpus, Is.EqualTo(new[] { 4, 5 }));
            Assert.That(Enumerable.Range(0, PerformanceCores.MaxCpus).Where(mask.Contains), Is.EqualTo(new[] { 4, 5 }));
        }
    }

    [TestCase(true, TestName = "TryBuildMask_Fastest_EqualSpeeds_EveryPerformanceCore")]
    [TestCase(false, TestName = "TryBuildMask_Fastest_UnknownSpeeds_EveryPerformanceCore")]
    public void TryBuildMask_Fastest_NoFavoredCore_KeepsThePerformanceCores(bool known)
    {
        Func<int, long?> speeds = known ? static cpu => cpu < 12 ? 68 : 40 : static _ => null;

        PerformanceCores.TryBuildMask(ProcessingCores.Fastest, PerformanceCpus, Allowed("0-19"), Siblings, out _, out int[] cpus, speeds);

        Assert.That(cpus, Is.EqualTo(Enumerable.Range(0, 12).ToArray()), "without a faster core it narrows as Performance does");
    }

    [Test]
    public void TryBuildMask_Fastest_FavoredCoreOutsideTheCpuset_FastestAllowedCore()
    {
        PerformanceCores.TryBuildMask(ProcessingCores.Fastest, PerformanceCpus, Allowed("0-3,6-19"), Siblings, out _, out int[] cpus,
            static cpu => cpu is 4 or 5 ? 72 : cpu is 8 or 9 ? 70 : cpu < 12 ? 68 : 40);

        Assert.That(cpus, Is.EqualTo(new[] { 8, 9 }));
    }

    [Test]
    public void TryExclude_LeavesTheFastestCoreToTheProcessingThread()
    {
        bool built = PerformanceCores.TryExclude(Enumerable.Range(0, 12).ToArray(), [4, 5], out PerformanceCores.CpuMask mask, out int[] rest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built, Is.True);
            Assert.That(rest, Is.EqualTo(new[] { 0, 1, 2, 3, 6, 7, 8, 9, 10, 11 }), "the near prewarm workers take every other performance core");
            Assert.That(mask.Contains(4) || mask.Contains(5), Is.False);
            Assert.That(PerformanceCores.TryExclude([0, 1], [0, 1], out _, out _), Is.False, "nothing left means no separate near set");
        }
    }
}
