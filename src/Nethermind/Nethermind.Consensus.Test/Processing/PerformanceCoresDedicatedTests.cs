// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Test.Scheduler;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

public class PerformanceCoresDedicatedTests
{
    [Test]
    public async Task Background_scheduler_defaults_to_background_cpus([Values] bool disableAffinity)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) Assert.Ignore("Native affinity requires Linux or Windows.");
        List<PerformanceCores.Cpu> cpus = PerformanceCores.ReadCpus();
        if (cpus.Count == 0) Assert.Ignore("CPU topology is unavailable.");
        int[] selected = disableAffinity ? [] : PerformanceCores.SelectDedicated(cpus, background: true);
        uint[] expected = selected.Length == 0 ? ReadNativeAffinity() : OperatingSystem.IsLinux()
            ? selected.Select(cpu => (uint)cpu).ToArray()
            : cpus.Where(cpu => selected.Contains(cpu.Id)).Select(cpu => cpu.CpuSetId).ToArray();
        IBranchProcessor processor = Substitute.For<IBranchProcessor>();
        IChainHeadInfoProvider head = Substitute.For<IChainHeadInfoProvider>();
        await using BackgroundTaskScheduler scheduler = disableAffinity
            ? new(processor, head, 1, 16, LimboLogs.Instance, ProcessingCores.All)
            : new(processor, head, 1, 16, LimboLogs.Instance);
        TaskCompletionSource<uint[]> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.That(scheduler.TryScheduleTask(default(TestRequest), async (_, _) =>
        {
            await Task.Yield();
            completed.SetResult(ReadNativeAffinity());
        }), Is.True);

        Assert.That(await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EquivalentTo(expected));
    }

    [Test]
    public async Task Scope_sets_and_restores_native_affinity_without_restricting_child_threads([Values] bool background, [Values(ProcessingCores.All, ProcessingCores.Performance)] ProcessingCores mode)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) Assert.Ignore("Native affinity requires Linux or Windows.");

        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread worker = new(() =>
        {
            try
            {
                List<PerformanceCores.Cpu> cpus = PerformanceCores.ReadCpus();
                if (cpus.Count == 0) Assert.Ignore("CPU topology is unavailable.");
                uint[] before = ReadNativeAffinity();
                int[] selected = mode == ProcessingCores.All ? [] : PerformanceCores.SelectDedicated(cpus, background);
                uint[] expected = selected.Length == 0 ? before : OperatingSystem.IsLinux()
                    ? selected.Select(cpu => (uint)cpu).ToArray()
                    : cpus.Where(cpu => selected.Contains(cpu.Id)).Select(cpu => cpu.CpuSetId).ToArray();

                TestLogger logger = new();
                using (PerformanceCores.Scope scope = PerformanceCores.NarrowDedicatedThread(mode, background, new(logger)))
                {
                    uint[] after = ReadNativeAffinity();
                    using (PerformanceCores.Scope nested = PerformanceCores.NarrowDedicatedThread(mode, background, new(logger)))
                        Assert.That(ReadNativeAffinity(), Is.EquivalentTo(expected));
                    Assert.That(ReadNativeAffinity(), Is.EquivalentTo(after), "nested scopes must retain the dedicated worker's affinity");
                    uint[]? childAffinity = null;
                    Exception? childException = null;
                    Thread child = new(() =>
                    {
                        try { childAffinity = ReadNativeAffinity(); }
                        catch (Exception exception) { childException = exception; }
                    })
                    { IsBackground = true };
                    child.Start();
                    Assert.That(child.Join(TimeSpan.FromSeconds(5)), Is.True);

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(logger.LogList, Has.None.Contains("Could not set"));
                        Assert.That(after, Is.EquivalentTo(expected), "the OS must report the selected CPU mask or CPU Set IDs");
                        Assert.That(childException, Is.Null);
                        Assert.That(childAffinity, Is.EquivalentTo(before), "CoreCLR resets a newly created managed thread's Linux affinity");
                    }
                }
                Assert.That(ReadNativeAffinity(), Is.EquivalentTo(before), "scope disposal restores the original affinity");
                completed.SetResult();
            }
            catch (Exception exception) { completed.SetException(exception); }
        })
        { IsBackground = true };
        worker.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static uint[] ReadNativeAffinity()
    {
        if (OperatingSystem.IsLinux())
        {
            byte[] mask = new byte[128];
            Assert.That(sched_getaffinity(0, (nuint)mask.Length, mask), Is.Zero);
            return Enumerable.Range(0, mask.Length * 8).Where(cpu => (mask[cpu / 8] & (1 << (cpu % 8))) != 0)
                .Select(cpu => (uint)cpu).ToArray();
        }

        GetThreadSelectedCpuSets(GetCurrentThread(), null, 0, out uint count);
        uint[] ids = new uint[count];
        Assert.That(GetThreadSelectedCpuSets(GetCurrentThread(), ids, count, out uint returned), Is.True);
        return ids.AsSpan(0, checked((int)returned)).ToArray();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_getaffinity(int pid, nuint size, [Out] byte[] mask);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadSelectedCpuSets(nint thread, [Out] uint[]? ids, uint count, out uint requiredCount);

    [TestCaseSource(nameof(AffinityCases))]
    public void Dedicated_worker_affinity_respects_physical_cores(int[] ids, int[] cores, int[] classes, int[] processing, int[] background)
    {
        List<PerformanceCores.Cpu> cpus = ids.Select((id, index) => new PerformanceCores.Cpu(id, cores[index], classes[index])).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PerformanceCores.SelectDedicated(cpus, false), Is.EqualTo(processing));
            Assert.That(PerformanceCores.SelectDedicated(cpus, true), Is.EqualTo(background));
        }
    }

    private static IEnumerable<TestCaseData> AffinityCases()
    {
        yield return new TestCaseData(new[] { 0, 1, 2, 3, 4, 5 }, new[] { 0, 0, 1, 1, 2, 3 }, new[] { 1, 1, 1, 1, 0, 0 }, new[] { 2 }, new[] { 4, 5 }).SetName("Affinity_hybrid_uses_second_physical_performance_core");
        yield return new TestCaseData(new[] { 0, 1, 2, 3 }, new[] { 0, 0, 1, 1 }, new[] { 0, 0, 0, 0 }, new[] { 2 }, new[] { 0, 1 }).SetName("Affinity_homogeneous_excludes_processing_sibling_from_background");
        yield return new TestCaseData(new[] { 1, 3, 4 }, new[] { 0, 1, 2 }, new[] { 1, 1, 0 }, new[] { 3 }, new[] { 4 }).SetName("Affinity_restricted_cpuset_uses_available_siblings");
        yield return new TestCaseData(new[] { 0, 1, 4 }, new[] { 0, 0, 2 }, new[] { 1, 1, 0 }, new[] { 1 }, new[] { 4 }).SetName("Affinity_one_performance_core_falls_back_to_first");
        yield return new TestCaseData(new[] { 7 }, new[] { 3 }, new[] { 0 }, new[] { 7 }, Array.Empty<int>()).SetName("Affinity_single_cpu_leaves_background_unrestricted");
        yield return new TestCaseData(new[] { 0, 1 }, new[] { 0, 0 }, new[] { 0, 0 }, new[] { 1 }, Array.Empty<int>()).SetName("Affinity_single_physical_core_leaves_background_unrestricted");
        yield return new TestCaseData(new[] { 4, 0, 2 }, new[] { 2, 0, 1 }, new[] { 512, 1024, 1024 }, new[] { 2 }, new[] { 4 }).SetName("Affinity_capacity_classes_and_unsorted_cpu_ids");
        yield return new TestCaseData(new[] { 0 }, new[] { 0 }, new[] { 0 }, new[] { 0 }, Array.Empty<int>()).SetName("Affinity_cpu_zero_is_only_fallback_when_no_alternative_exists");
        yield return new TestCaseData(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()).SetName("Affinity_missing_topology_leaves_workers_unrestricted");
    }

    [Test]
    public void Windows_affinity_keeps_processor_groups_and_cpu_set_ids_distinct([Values] bool hybrid)
    {
        byte[] topology = [.. WindowsCpu(100, 0, 0, 0, 1), .. WindowsCpu(101, 0, 1, 0, 1),
            .. WindowsCpu(200, 1, 0, 0, 1), .. WindowsCpu(201, 1, 1, 0, 1),
            .. WindowsCpu(300, 2, 0, 0, hybrid ? (byte)0 : (byte)1)];
        List<PerformanceCores.Cpu> cpus = PerformanceCores.ParseWindowsCpus(topology, [], null);
        PerformanceCores.Selection?[] selections = new PerformanceCores.Selection?[Enum.GetValues<ProcessingCores>().Length];
        PerformanceCores.BuildWindowsSelections(cpus, selections, out PerformanceCores.PrewarmSplit? prewarm, out PerformanceCores.PrewarmSplit? prewarmDedicated);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(selections[(int)ProcessingCores.Performance]?.Cpus, Is.EqualTo(hybrid ? new[] { 0, 1, 64, 65 } : null));
            Assert.That(selections[(int)ProcessingCores.PerformancePhysical]?.Cpus, Is.EqualTo(hybrid ? new[] { 0, 64 } : null));
            Assert.That(prewarm?.Far.Cpus, Is.EqualTo(hybrid ? new[] { 128 } : null));
            Assert.That(selections[(int)ProcessingCores.Dedicated]?.Cpus, Is.EqualTo(hybrid ? new[] { 64, 65 } : null));
            Assert.That(prewarmDedicated?.Near.Cpus, Is.EqualTo(hybrid ? new[] { 0, 1 } : null));
            Assert.That(PerformanceCores.SelectDedicated(cpus, false), Is.EqualTo(new[] { 64 }));
            Assert.That(PerformanceCores.SelectDedicated(cpus, true), Is.EqualTo(hybrid ? new[] { 128 } : new[] { 0, 1, 128 }));
            Assert.That(cpus.Single(cpu => cpu.Id == 64).CpuSetId, Is.EqualTo(200));
        }
    }

    [Test]
    public void Windows_affinity_respects_default_cpu_sets_and_reservations()
    {
        byte[] topology = [.. WindowsCpu(100, 0, 0, 0, 1), .. WindowsCpu(200, 1, 0, 0, 1),
            .. WindowsCpu(201, 1, 1, 1, 1, flags: 2), .. WindowsCpu(202, 1, 2, 2, 1, flags: 6)];
        List<PerformanceCores.Cpu> cpus = PerformanceCores.ParseWindowsCpus(topology, [200, 201, 202], null);
        Assert.That(cpus.Select(cpu => cpu.Id), Is.EqualTo(new[] { 64, 66 }));
    }

    [Test]
    public void Windows_affinity_preserves_bit_63_of_a_restricted_mask()
    {
        byte[] topology = [.. WindowsCpu(100, 0, 0, 0, 1), .. WindowsCpu(163, 0, 63, 31, 1), .. WindowsCpu(200, 1, 0, 0, 1)];
        List<PerformanceCores.Cpu> cpus = PerformanceCores.ParseWindowsCpus(topology, [], 1UL << 63);
        Assert.That(PerformanceCores.SelectDedicated(cpus, false), Is.EqualTo(new[] { 63 }));
    }

    private static byte[] WindowsCpu(uint id, ushort group, byte cpu, byte core, byte performanceClass, byte flags = 0)
    {
        byte[] entry = new byte[32];
        BitConverter.TryWriteBytes(entry.AsSpan(), entry.Length);
        BitConverter.TryWriteBytes(entry.AsSpan(8), id);
        BitConverter.TryWriteBytes(entry.AsSpan(12), group);
        entry[14] = cpu;
        entry[15] = core;
        entry[18] = performanceClass;
        entry[19] = flags;
        return entry;
    }

}
