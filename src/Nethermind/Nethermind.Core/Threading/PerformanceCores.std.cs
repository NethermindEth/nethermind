// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Threading;

public static partial class PerformanceCores
{
    private static readonly bool _narrows;
    private static readonly CpuMask _mask;
    private static readonly int[] _cpus = [];

    static PerformanceCores()
    {
        if (OperatingSystem.IsLinux())
        {
            _narrows = TryBuildMask(ReadOrNull("/sys/devices/cpu_core/cpus"), ReadAllowedCpus(), out _mask, out _cpus);
        }
    }

    /// <summary>The performance cores' logical processors the processing thread runs on; empty where nothing is narrowed.</summary>
    public static ReadOnlySpan<int> Cpus => _cpus;

    /// <summary>
    /// Keeps the calling thread on the performance cores until the scope is disposed, on the same thread. Does
    /// nothing where there is nothing to narrow, and where the kernel refuses.
    /// </summary>
    public static Scope NarrowCurrentThread()
    {
        if (!_narrows) return default;

        // pid 0 is the calling thread.
        if (sched_getaffinity(0, CpuMaskSize, out CpuMask previous) != 0) return default;
        CpuMask mask = _mask;
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

    private static readonly nint CpuMaskSize = MaskWords * sizeof(ulong);

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

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_getaffinity(int pid, nint cpusetsize, out CpuMask mask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, nint cpusetsize, ref CpuMask mask);
}
