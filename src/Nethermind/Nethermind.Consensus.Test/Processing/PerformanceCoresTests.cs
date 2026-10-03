// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

    [TestCase("0-19", new[] { 2, 3 }, TestName = "TryBuildMask_Dedicated_Unpinned_SecondCoreSinceCpu0TakesInterrupts")]
    [TestCase("0-1,4-19", new[] { 4, 5 }, TestName = "TryBuildMask_Dedicated_Cpuset_FirstAllowedCoreWithoutCpu0")]
    [TestCase("1-19", new[] { 2, 3 }, TestName = "TryBuildMask_Dedicated_Cpu1Allowed_StillSkipsTheCoreOfCpu0")]
    [TestCase("0-1,12-19", new[] { 0, 1 }, TestName = "TryBuildMask_Dedicated_OnlyTheCoreOfCpu0_UsesIt")]
    public void TryBuildMask_Dedicated_PicksOneCoreByPosition(string allowedCpus, int[] expected)
    {
        bool narrows = PerformanceCores.TryBuildMask(ProcessingCores.Dedicated, PerformanceCpus, Allowed(allowedCpus), Siblings, out PerformanceCores.CpuMask mask, out int[] cpus);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(narrows, Is.True);
            Assert.That(cpus, Is.EqualTo(expected));
            Assert.That(Enumerable.Range(0, PerformanceCores.MaxCpus).Where(mask.Contains), Is.EqualTo(expected));
        }
    }

    [Test]
    public void TryBuildMask_Dedicated_SiblingsNumberedAfterEveryCore_SkipsCpu0sSibling()
    {
        // 6 performance cores numbered 0-5, their second hyperthreads 6-11: CPU 6 is CPU 0's sibling, CPU 1 is a core of its own.
        static string Interleaved(int cpu) => cpu < 12 ? $"{cpu % 6},{cpu % 6 + 6}" : $"{cpu}";

        bool narrows = PerformanceCores.TryBuildMask(ProcessingCores.Dedicated, "0-11", Allowed("0-19"), Interleaved, out _, out int[] cpus);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(narrows, Is.True);
            Assert.That(cpus, Is.EqualTo(new[] { 1, 7 }), "the core of CPU 1 and its sibling 7, not CPU 0's sibling 6");
        }
    }

    [Test]
    public void TryBuildMask_Dedicated_NoHyperthreading_Cpu1()
    {
        bool narrows = PerformanceCores.TryBuildMask(ProcessingCores.Dedicated, "0-5", Allowed("0-13"), static cpu => $"{cpu}", out _, out int[] cpus);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(narrows, Is.True);
            Assert.That(cpus, Is.EqualTo(new[] { 1 }));
        }
    }

    [Test]
    public void TryBuildMask_Dedicated_SiblingsUnknown_DoesNotNarrow() =>
        Assert.That(PerformanceCores.TryBuildMask(ProcessingCores.Dedicated, PerformanceCpus, Allowed("0-19"), static _ => null, out _, out _), Is.False);

    /// <summary>One kind of core, hyperthreads numbered after every core as on most AMD CPUs: 0 and 8 share a core.</summary>
    [Test]
    public void TryBuildUniformDedicated_GivesTheProcessingThreadACoreAndPrewarmTheRest()
    {
        static string Uniform(int cpu) => $"{cpu % 8},{cpu % 8 + 8}";
        bool built = PerformanceCores.TryBuildUniformDedicated(PerformanceCores.ParseCpuList("0-15"), Uniform,
            out PerformanceCores.Selection dedicated, out PerformanceCores.PrewarmSplit prewarm);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built, Is.True);
            Assert.That(dedicated.Cpus, Is.EqualTo(new[] { 1, 9 }), "both hyperthreads of the first core without CPU 0");
            Assert.That(prewarm.Near.Cpus, Is.EqualTo(new[] { 0, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15 }));
            Assert.That(prewarm.Far.Cpus, Is.EqualTo(prewarm.Near.Cpus), "every prewarm worker stays off the processing core");
            Assert.That(prewarm.NearWorkers, Is.EqualTo(14));
        }
    }

    /// <param name="pinning">The helper thread's mask before the pass: every CPU, the dedicated one and one other, or the dedicated one alone.</param>
    [TestCase("all", TestName = "MoveOffDedicatedCore_UnpinnedThread_TakesTheOtherCpus")]
    [TestCase("pair", TestName = "MoveOffDedicatedCore_ThreadPinnedElsewhereToo_KeepsItsOtherCpu")]
    [TestCase("dedicated", TestName = "MoveOffDedicatedCore_ThreadPinnedToTheDedicatedCpuOnly_GoesToTheOthers")]
    public void MoveOffDedicatedCore_LeavesTheKeptThreadAlone(string pinning)
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Thread affinity is read and set through Linux system calls.");
        Assert.That(PerformanceCores.TryGetAffinity(0, out PerformanceCores.CpuMask allowed), Is.True);
        int[] cpus = Enumerable.Range(0, PerformanceCores.MaxCpus).Where(cpu => allowed.Contains(cpu)).ToArray();
        if (cpus.Length < 2) Assert.Ignore("A thread needs two CPUs to be moved between.");

        PerformanceCores.CpuMask dedicated = default;
        dedicated.Add(cpus[^1]);
        PerformanceCores.CpuMask others = default;
        foreach (int cpu in cpus[..^1]) others.Add(cpu);
        PerformanceCores.CpuMask start = default;
        switch (pinning)
        {
            case "all":
                start = allowed;
                break;
            case "pair":
                start.Add(cpus[0]);
                start.Add(cpus[^1]);
                break;
            default:
                start.Add(cpus[^1]);
                break;
        }

        PerformanceCores.CpuMask expected = default;
        switch (pinning)
        {
            case "pair":
                expected.Add(cpus[0]);
                break;
            default:
                expected = others;
                break;
        }

        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();
        int otherThread = 0;
        Thread thread = new(() =>
        {
            PerformanceCores.TryGetCurrentThreadId(out otherThread);
            PerformanceCores.TrySetAffinity(0, start);
            started.Set();
            release.Wait();
        });
        thread.Start();
        try
        {
            started.Wait();
            Assert.That(PerformanceCores.TryGetCurrentThreadId(out int self), Is.True);
            Assert.That(PerformanceCores.TryGetAffinity(self, out PerformanceCores.CpuMask selfBefore), Is.True);
            int moved = PerformanceCores.MoveOffDedicatedCore([otherThread, self], others, dedicated, keep: self);

            Assert.That(PerformanceCores.TryGetAffinity(otherThread, out PerformanceCores.CpuMask otherMask), Is.True);
            Assert.That(PerformanceCores.TryGetAffinity(self, out PerformanceCores.CpuMask selfAfter), Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(moved, Is.EqualTo(1));
                Assert.That(otherMask.Overlaps(dedicated), Is.False, "the other thread leaves the dedicated core");
                Assert.That(otherMask.Overlaps(expected) && !otherMask.Without(expected).Overlaps(allowed), Is.True, "the rest of its mask is kept, or the others when nothing is left");
                Assert.That(selfAfter.Overlaps(selfBefore) && !selfAfter.Without(selfBefore).Overlaps(allowed), Is.True, "the kept thread keeps its mask");
            }
        }
        finally
        {
            release.Set();
            thread.Join();
        }
    }

    [Test]
    public void TryBuildUniformDedicated_TooFewCpusLeft_DoesNotNarrow() =>
        Assert.That(PerformanceCores.TryBuildUniformDedicated(PerformanceCores.ParseCpuList("0-3"), static cpu => $"{cpu % 2},{cpu % 2 + 2}", out _, out _), Is.False);

    /// <summary>Three cores of two hyperthreads leave the rest of the node four.</summary>
    [Test]
    public void TryBuildUniformDedicated_FourCpusLeft_Narrows() =>
        Assert.That(PerformanceCores.TryBuildUniformDedicated(PerformanceCores.ParseCpuList("0-5"), static cpu => $"{cpu % 3},{cpu % 3 + 3}", out _, out _), Is.True);

    [Test]
    public void TryBuildUniformDedicated_SiblingsUnknown_DoesNotNarrow() =>
        Assert.That(PerformanceCores.TryBuildUniformDedicated(PerformanceCores.ParseCpuList("0-15"), static _ => null, out _, out _), Is.False);

    [Test]
    public void TryBuildUniformDedicated_OneCoreAllowed_HasNothingToLeavePrewarm() =>
        Assert.That(PerformanceCores.TryBuildUniformDedicated(PerformanceCores.ParseCpuList("1,9"), static cpu => "1,9", out _, out _), Is.False);

    [Test]
    public void TryExclude_LeavesTheDedicatedCoreToTheProcessingThread()
    {
        bool built = PerformanceCores.TryExclude(Enumerable.Range(0, 12).ToArray(), [2, 3], out PerformanceCores.CpuMask mask, out int[] rest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built, Is.True);
            Assert.That(rest, Is.EqualTo(new[] { 0, 1, 4, 5, 6, 7, 8, 9, 10, 11 }), "the near prewarm workers take every other performance core");
            Assert.That(mask.Contains(2) || mask.Contains(3), Is.False);
            Assert.That(PerformanceCores.TryExclude([0, 1], [0, 1], out _, out _), Is.False, "nothing left means no separate near set");
        }
    }
}
