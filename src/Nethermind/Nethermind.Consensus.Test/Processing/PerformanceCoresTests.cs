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

    [Test]
    public void WidenInheritors_PutsBackOnlyThreadsHoldingTheNarrowedMask()
    {
        PerformanceCores.CpuMask narrowed = Mask(0, 2, 4);
        PerformanceCores.CpuMask target = Mask(Enumerable.Range(0, 20).ToArray());
        PerformanceCores.CpuMask other = Mask(12, 13);
        // 1 inherited the narrowed mask, 2 was pinned elsewhere, 3 exited before the scan read it.
        Dictionary<int, PerformanceCores.CpuMask> threads = new() { [1] = narrowed, [2] = other };

        int widened = PerformanceCores.WidenInheritors([1, 2, 3], [narrowed], target, NotScoped,
            (int tid, out PerformanceCores.CpuMask mask) => threads.TryGetValue(tid, out mask) ? 0 : -1,
            (int tid, ref PerformanceCores.CpuMask mask) =>
            {
                if (!threads.ContainsKey(tid)) return -1;
                threads[tid] = mask;
                return 0;
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(widened, Is.EqualTo(1));
            Assert.That(threads[1].SequenceEqual(target), Is.True, "the inheritor is put back on the target");
            Assert.That(threads[2].SequenceEqual(other), Is.True, "a thread with a mask of its own is left alone");
        }
    }

    [Test]
    public void WidenInheritors_LeavesThreadsInsideAScopeAlone()
    {
        PerformanceCores.CpuMask performance = Mask(Enumerable.Range(0, 12).ToArray());
        PerformanceCores.CpuMask efficiency = Mask(Enumerable.Range(12, 8).ToArray());
        PerformanceCores.CpuMask target = Mask(Enumerable.Range(0, 20).ToArray());
        // 1 is the processing thread inside its scope, 2 a prewarm worker inside its scope, 3 inherited the efficiency
        // mask from a worker and is in no scope.
        Dictionary<int, PerformanceCores.CpuMask> threads = new() { [1] = performance, [2] = efficiency, [3] = efficiency };

        int widened = PerformanceCores.WidenInheritors([1, 2, 3], [performance, efficiency], target,
            (int tid, out PerformanceCores.CpuMask wanted) =>
            {
                wanted = tid == 1 ? performance : efficiency;
                return tid is 1 or 2 ? PerformanceCores.ScopeState.Narrowed : PerformanceCores.ScopeState.None;
            },
            (int tid, out PerformanceCores.CpuMask mask) => threads.TryGetValue(tid, out mask) ? 0 : -1,
            (int tid, ref PerformanceCores.CpuMask mask) =>
            {
                threads[tid] = mask;
                return 0;
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(widened, Is.EqualTo(1));
            Assert.That(threads[1].SequenceEqual(performance), Is.True, "a scope ending elsewhere must not widen the processing thread mid-block");
            Assert.That(threads[2].SequenceEqual(efficiency), Is.True, "nor a worker still inside its scope");
            Assert.That(threads[3].SequenceEqual(target), Is.True, "an inheritor of any narrowed mask is put back");
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

    [Test]
    public void RestoreTarget_ThreadEnteringWithANarrowedMask_GoesBackToTheAllowedSet()
    {
        PerformanceCores.CpuMask performance = Mask(Enumerable.Range(0, 12).ToArray());
        PerformanceCores.CpuMask efficiency = Mask(Enumerable.Range(12, 8).ToArray());
        PerformanceCores.CpuMask allowed = Mask(Enumerable.Range(0, 20).ToArray());
        PerformanceCores.CpuMask pinnedByOperator = Mask(0, 1, 12, 13);
        PerformanceCores.CpuMask[] narrowed = [performance, efficiency];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PerformanceCores.RestoreTarget(performance, narrowed, allowed).SequenceEqual(allowed), Is.True,
                "a prewarm worker started inside the processing scope must not be restored onto the performance cores");
            Assert.That(PerformanceCores.RestoreTarget(efficiency, narrowed, allowed).SequenceEqual(allowed), Is.True);
            Assert.That(PerformanceCores.RestoreTarget(pinnedByOperator, narrowed, allowed).SequenceEqual(pinnedByOperator), Is.True,
                "a mask of the thread's own is restored as it was");
        }
    }

    [Test]
    public void WidenInheritors_ThreadEnteringItsScopeMidScan_KeepsItsScopesMask()
    {
        PerformanceCores.CpuMask performance = Mask(Enumerable.Range(0, 12).ToArray());
        PerformanceCores.CpuMask target = Mask(Enumerable.Range(0, 20).ToArray());
        // The processing loop resumes on an inheritor: outside any scope when the scan checks it, inside its scope,
        // narrowed, by the time the scan has written the target.
        Dictionary<int, PerformanceCores.CpuMask> threads = new() { [1] = performance };
        int checks = 0;

        int widened = PerformanceCores.WidenInheritors([1], [performance], target,
            (int tid, out PerformanceCores.CpuMask wanted) =>
            {
                wanted = performance;
                return checks++ == 0 ? PerformanceCores.ScopeState.None : PerformanceCores.ScopeState.Narrowed;
            },
            (int tid, out PerformanceCores.CpuMask mask) => threads.TryGetValue(tid, out mask) ? 0 : -1,
            (int tid, ref PerformanceCores.CpuMask mask) =>
            {
                threads[tid] = mask;
                return 0;
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(threads[1].SequenceEqual(performance), Is.True, "a block must not run unpinned because a scan raced its scope");
            Assert.That(widened, Is.Zero);
        }
    }

    [Test]
    public void WidenInheritors_ThreadStillEnteringItsScope_IsLeftToNarrowItself()
    {
        PerformanceCores.CpuMask performance = Mask(Enumerable.Range(0, 12).ToArray());
        PerformanceCores.CpuMask target = Mask(Enumerable.Range(0, 20).ToArray());
        Dictionary<int, PerformanceCores.CpuMask> threads = new() { [1] = performance };
        int writes = 0;

        PerformanceCores.WidenInheritors([1], [performance], target,
            (int tid, out PerformanceCores.CpuMask wanted) =>
            {
                wanted = default;
                return PerformanceCores.ScopeState.Entering;
            },
            (int tid, out PerformanceCores.CpuMask mask) => threads.TryGetValue(tid, out mask) ? 0 : -1,
            (int tid, ref PerformanceCores.CpuMask mask) =>
            {
                writes++;
                return 0;
            });

        Assert.That(writes, Is.Zero, "a thread publishing its scope narrows itself next, so the scan must not touch it");
    }

    private static PerformanceCores.ScopeState NotScoped(int tid, out PerformanceCores.CpuMask wanted)
    {
        wanted = default;
        return PerformanceCores.ScopeState.None;
    }

    private static PerformanceCores.CpuMask Mask(params int[] cpus)
    {
        PerformanceCores.CpuMask mask = default;
        foreach (int cpu in cpus) mask.Add(cpu);
        return mask;
    }
}
