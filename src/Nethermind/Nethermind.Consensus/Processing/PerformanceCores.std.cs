// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Config;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// The performance cores of an Intel hybrid CPU, and a scope that keeps the calling thread on them.
/// </summary>
/// <remarks>
/// The processing loop continues on whichever thread-pool thread resumes it, and on a CPU with performance and
/// efficiency cores the scheduler can start that thread on an efficiency core for a block that lasts tens of
/// milliseconds. The core types come from Linux's Intel hybrid PMU listing (<c>/sys/devices/cpu_core/cpus</c>);
/// other hybrid designs do not publish it, and there this is a no-op. The allowed CPUs are read once, so a cpuset
/// changed at runtime is not seen; the kernel then refuses a mask outside it and the scope does nothing.
/// </remarks>
internal static partial class PerformanceCores
{
    /// <summary>The CPUs a Linux <c>cpu_set_t</c> holds.</summary>
    internal const int MaxCpus = MaskWords * 64;
    private const int MaskWords = 16;
    private static readonly nint CpuMaskSize = MaskWords * sizeof(ulong);
    private static readonly int ModeCount = Enum.GetValues<ProcessingCores>().Length;

    [InlineArray(MaskWords)]
    internal struct CpuMask
    {
        private ulong _word;

        public void Add(int cpu) => this[cpu >> 6] |= 1UL << (cpu & 63);

        public readonly bool Contains(int cpu) => (this[cpu >> 6] & (1UL << (cpu & 63))) != 0;

        public readonly bool SequenceEqual(in CpuMask other)
        {
            ReadOnlySpan<ulong> self = this;
            ReadOnlySpan<ulong> that = other;
            return self.SequenceEqual(that);
        }
    }

    /// <summary>The logical processors <paramref name="cores"/> narrows the processing thread to; empty where it narrows nothing.</summary>
    public static ReadOnlySpan<int> Cpus(ProcessingCores cores) => Selected(cores)?.Cpus ?? [];

    /// <summary>
    /// Keeps the calling thread on the logical processors <paramref name="cores"/> selects until the scope is disposed,
    /// on the same thread. Does nothing where that narrows nothing, and where the kernel refuses.
    /// </summary>
    public static Scope NarrowCurrentThread(ProcessingCores cores)
    {
        if (Selected(cores) is not { } selection) return default;

        // pid 0 is the calling thread.
        if (sched_getaffinity(0, CpuMaskSize, out CpuMask previous) != 0) return default;
        int threads = ReadThreadCount();
        int poolThreads = ThreadPool.ThreadCount;
        CpuMask mask = selection.Mask;
        return sched_setaffinity(0, CpuMaskSize, ref mask) == 0 ? new Scope(selection, previous, threads, poolThreads) : default;
    }

    public readonly struct Scope : IDisposable
    {
        private readonly Selection? _selection;
        private readonly CpuMask _previous;
        private readonly int _threads;
        private readonly int _poolThreads;

        internal Scope(Selection selection, CpuMask previous, int threads, int poolThreads)
        {
            _selection = selection;
            _previous = previous;
            _threads = threads;
            _poolThreads = poolThreads;
        }

        public void Dispose()
        {
            if (_selection is null) return;

            CpuMask previous = _previous;
            sched_setaffinity(0, CpuMaskSize, ref previous);

            // A thread started from inside the scope inherits the narrowed mask: the thread pool injects its workers on
            // the thread that queues the work, and processing queues plenty. Those are put back, so the scope narrows
            // this thread alone. A thread count that moved is the cheap sign; the scan runs only then.
            if (ReadThreadCount() != _threads || ThreadPool.ThreadCount != _poolThreads)
                WidenInheritors(_selection.Mask, previous);
        }
    }

    internal sealed class Selection(CpuMask mask, int[] cpus)
    {
        public CpuMask Mask { get; } = mask;
        public int[] Cpus { get; } = cpus;
    }

    /// <summary>Built on first use by a mode that narrows, so the default leaves the host untouched.</summary>
    private static class Host
    {
        public static readonly Selection?[] Selections = Build();

        private static Selection?[] Build()
        {
            Selection?[] selections = new Selection?[ModeCount];
            if (!OperatingSystem.IsLinux()) return selections;

            string? performanceCpus = ReadOrNull("/sys/devices/cpu_core/cpus");
            if (performanceCpus is null || !CanReadAffinity()) return selections;

            string? allowedCpus = ReadAllowedCpus();
            foreach (ProcessingCores cores in Enum.GetValues<ProcessingCores>())
            {
                if (TryBuildMask(cores, performanceCpus, allowedCpus, ReadSiblings, out CpuMask mask, out int[] cpus))
                    selections[(int)cores] = new Selection(mask, cpus);
            }

            return selections;
        }
    }

    // Any value the enum does not name - the config binder accepts numbers - narrows nothing.
    private static Selection? Selected(ProcessingCores cores) =>
        cores != ProcessingCores.All && (uint)cores < (uint)ModeCount ? Host.Selections[(int)cores] : null;

    /// <summary>
    /// The logical processors <paramref name="cores"/> selects among the performance cores the process may run on,
    /// when that narrows anything.
    /// </summary>
    /// <param name="cores">Which of the performance cores' logical processors to run on.</param>
    /// <param name="performanceCpus">Linux's list of the performance cores' logical processors, null on a CPU without one.</param>
    /// <param name="allowedCpus">The CPUs the process may run on, null when unknown.</param>
    /// <param name="siblingsOf">A logical processor's hyperthread siblings as a Linux CPU list, null when unknown.</param>
    /// <param name="mask">The logical processors to run on.</param>
    /// <param name="cpus">The same, as a sorted list.</param>
    /// <returns>
    /// <c>false</c> for <see cref="ProcessingCores.All"/>, on a CPU with one kind of core, and where the selection
    /// holds every CPU the process may run on or none of them, since there is then nothing to narrow.
    /// </returns>
    internal static bool TryBuildMask(ProcessingCores cores, string? performanceCpus, string? allowedCpus, Func<int, string?> siblingsOf, out CpuMask mask, out int[] cpus)
    {
        mask = default;
        cpus = [];
        if (cores == ProcessingCores.All || performanceCpus is null) return false;

        HashSet<int> selected = ParseCpuList(performanceCpus);
        int allowedCount = int.MaxValue;
        if (allowedCpus is not null)
        {
            HashSet<int> allowed = ParseCpuList(allowedCpus);
            if (allowed.Count > 0)
            {
                selected.IntersectWith(allowed);
                allowedCount = allowed.Count;
            }
        }

        if (cores == ProcessingCores.PerformancePhysical)
        {
            // The lowest selected hyperthread of each core stands for the core.
            HashSet<int> performance = [.. selected];
            selected.RemoveWhere(cpu => siblingsOf(cpu) is { } siblings && ParseCpuList(siblings).Any(sibling => sibling < cpu && performance.Contains(sibling)));
        }

        if (selected.Count == 0 || selected.Count == allowedCount) return false;

        cpus = [.. selected];
        Array.Sort(cpus);
        foreach (int cpu in cpus) mask.Add(cpu);
        return true;
    }

    /// <summary>Parses a Linux CPU list such as <c>0-11,14</c>, skipping malformed entries and CPUs a mask cannot hold.</summary>
    internal static HashSet<int> ParseCpuList(string cpuList)
    {
        HashSet<int> cpus = [];
        foreach (string range in cpuList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = range.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(range, out int cpu) && cpu is >= 0 and < MaxCpus) cpus.Add(cpu);
            }
            else if (int.TryParse(range.AsSpan(0, dash), out int first) && int.TryParse(range.AsSpan(dash + 1), out int last)
                     && first >= 0 && last >= first)
            {
                for (int cpu = first; cpu <= Math.Min(last, MaxCpus - 1); cpu++) cpus.Add(cpu);
            }
        }

        return cpus;
    }

    /// <summary>The thread count from <c>/proc/self/stat</c>, the twentieth field and the seventeenth after the command name.</summary>
    internal static int ParseThreadCount(string stat)
    {
        // The command name is parenthesised and may itself hold spaces and parentheses, so fields count from the last ')'.
        int end = stat.LastIndexOf(')');
        if (end < 0) return -1;

        string[] fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 17 && int.TryParse(fields[17], out int threads) ? threads : -1;
    }

    private static int ReadThreadCount() => ReadOrNull("/proc/self/stat") is { } stat ? ParseThreadCount(stat) : -1;

    /// <summary>Puts every thread of the process that still holds the narrowed mask back on <paramref name="previous"/>.</summary>
    private static void WidenInheritors(in CpuMask narrowed, CpuMask previous)
    {
        IEnumerable<string> tasks;
        try
        {
            tasks = Directory.EnumerateDirectories("/proc/self/task");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string task in tasks)
        {
            // A thread that exited meanwhile fails the calls and is skipped.
            if (!int.TryParse(Path.GetFileName(task), out int tid)) continue;
            if (sched_getaffinity(tid, CpuMaskSize, out CpuMask current) == 0 && current.SequenceEqual(narrowed))
                sched_setaffinity(tid, CpuMaskSize, ref previous);
        }
    }

    /// <summary>
    /// Whether the thread's affinity can be read here: the call can be missing from the C library, and the processing
    /// loop must not find that out block by block. A kernel that refuses the write makes the scope a no-op instead.
    /// </summary>
    private static bool CanReadAffinity()
    {
        try
        {
            return sched_getaffinity(0, CpuMaskSize, out _) == 0;
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
