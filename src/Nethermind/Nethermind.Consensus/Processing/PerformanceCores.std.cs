// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.InteropServices;
using Nethermind.Config;

namespace Nethermind.Consensus.Processing;

internal static partial class PerformanceCores
{
    private static readonly nint CpuMaskSize = MaskWords * sizeof(ulong);
    private static readonly Selection[] _selections = new Selection[Enum.GetValues<ProcessingCores>().Length];

    static PerformanceCores()
    {
        if (!OperatingSystem.IsLinux() || !CanSetAffinity()) return;

        string? performanceCpus = ReadOrNull("/sys/devices/cpu_core/cpus");
        string? allowedCpus = ReadAllowedCpus();
        foreach (ProcessingCores cores in Enum.GetValues<ProcessingCores>())
        {
            if (TryBuildMask(cores, performanceCpus, allowedCpus, ReadSiblings, out CpuMask mask, out int[] cpus))
                _selections[(int)cores] = new Selection(mask, cpus);
        }
    }

    /// <summary>The logical processors <paramref name="cores"/> narrows the processing thread to; empty where it narrows nothing.</summary>
    public static ReadOnlySpan<int> Cpus(ProcessingCores cores) => _selections[(int)cores]?.Cpus ?? [];

    /// <summary>
    /// Keeps the calling thread on the logical processors <paramref name="cores"/> selects until the scope is disposed,
    /// on the same thread. Does nothing where that narrows nothing, and where the kernel refuses.
    /// </summary>
    public static Scope NarrowCurrentThread(ProcessingCores cores)
    {
        if (_selections[(int)cores] is not { } selection) return default;

        // pid 0 is the calling thread.
        if (sched_getaffinity(0, CpuMaskSize, out CpuMask previous) != 0) return default;
        CpuMask mask = selection.Mask;
        return sched_setaffinity(0, CpuMaskSize, ref mask) == 0 ? new Scope(previous) : default;
    }

    public readonly struct Scope : IDisposable
    {
        private readonly CpuMask _previous;
        private readonly bool _narrowed;

        internal Scope(CpuMask previous)
        {
            _previous = previous;
            _narrowed = true;
        }

        public void Dispose()
        {
            if (!_narrowed) return;

            CpuMask previous = _previous;
            sched_setaffinity(0, CpuMaskSize, ref previous);
        }
    }

    private sealed record Selection(CpuMask Mask, int[] Cpus);

    /// <summary>
    /// Whether the thread's affinity can be read and written here: the call can be missing from the C library or
    /// refused by a seccomp profile, and the processing loop must not find that out block by block.
    /// </summary>
    private static bool CanSetAffinity()
    {
        try
        {
            return sched_getaffinity(0, CpuMaskSize, out CpuMask current) == 0 && sched_setaffinity(0, CpuMaskSize, ref current) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static string? ReadSiblings(int cpu) => ReadOrNull($"/sys/devices/system/cpu/cpu{cpu}/topology/thread_siblings_list");

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        // Absent or unreadable: as far as this process can tell the CPU has one kind of core.
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The CPUs the process may run on, which a cpuset or an affinity mask narrows below the host's.</summary>
    private static string? ReadAllowedCpus()
    {
        const string prefix = "Cpus_allowed_list:";
        string? status = ReadOrNull("/proc/self/status");
        if (status is null) return null;

        foreach (string line in status.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line[prefix.Length..];
        }

        return null;
    }

    [DllImport("libc")]
    private static extern int sched_getaffinity(int pid, nint cpusetsize, out CpuMask mask);

    [DllImport("libc")]
    private static extern int sched_setaffinity(int pid, nint cpusetsize, ref CpuMask mask);
}
