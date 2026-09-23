// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Config;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// The performance cores of an Intel hybrid CPU, and a scope that keeps the calling thread on them.
/// </summary>
/// <remarks>
/// The processing loop continues on whichever thread-pool thread resumes it, and on a CPU with performance and
/// efficiency cores the scheduler can start that thread on an efficiency core for a block that lasts tens of
/// milliseconds. The core types come from Linux's Intel hybrid PMU listing (<c>/sys/devices/cpu_core/cpus</c>);
/// other hybrid designs do not publish it, and there this is a no-op. The allowed CPUs are read once, so a cpuset
/// changed at runtime is not seen; the kernel then refuses a mask outside it and the scope does nothing, and a restore
/// it refuses falls back to every CPU the cpuset allows.
/// </remarks>
internal static partial class PerformanceCores
{
    /// <summary>The CPUs a Linux <c>cpu_set_t</c> holds.</summary>
    internal const int MaxCpus = MaskWords * 64;
    private const int MaskWords = 16;
    private static readonly nint CpuMaskSize = MaskWords * sizeof(ulong);
    private static readonly int ModeCount = Enum.GetValues<ProcessingCores>().Length;

    // The widening scan runs on the thread pool, one at a time; a request made while one runs makes it go again.
    private static int _scanRequested;
    private static int _scanRunning;
    private static Selection? _widenFrom;
    private static CpuMask _widenTo;
    private static int _restoreRefused;

    internal delegate int GetAffinity(int tid, out CpuMask mask);
    internal delegate int SetAffinity(int tid, ref CpuMask mask);

    [InlineArray(MaskWords)]
    internal struct CpuMask
    {
        private ulong _word;

        public void Add(int cpu) => this[cpu >> 6] |= 1UL << (cpu & 63);

        public static CpuMask Every()
        {
            CpuMask mask = default;
            ((Span<ulong>)mask).Fill(ulong.MaxValue);
            return mask;
        }

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
    public static Scope NarrowCurrentThread(ProcessingCores cores, ILogger logger)
    {
        if (Selected(cores) is not { } selection) return default;

        // pid 0 is the calling thread.
        if (sched_getaffinity(0, CpuMaskSize, out CpuMask previous) != 0) return default;
        CpuMask mask = selection.Mask;
        return sched_setaffinity(0, CpuMaskSize, ref mask) == 0 ? new Scope(selection, previous, logger) : default;
    }

    public readonly struct Scope : IDisposable
    {
        private readonly Selection? _selection;
        private readonly CpuMask _previous;
        private readonly ILogger _logger;

        internal Scope(Selection selection, CpuMask previous, ILogger logger)
        {
            _selection = selection;
            _previous = previous;
            _logger = logger;
        }

        public void Dispose()
        {
            if (_selection is null) return;

            CpuMask restored = _previous;
            if (sched_setaffinity(0, CpuMaskSize, ref restored) != 0)
            {
                // A cpuset changed at runtime to exclude the previous mask; the kernel narrows every CPU to the new cpuset.
                restored = CpuMask.Every();
                sched_setaffinity(0, CpuMaskSize, ref restored);
                if (Interlocked.Exchange(ref _restoreRefused, 1) == 0 && _logger.IsWarn)
                    _logger.Warn("Block processing could not restore its thread's CPU affinity, so it widened the thread to every CPU the cpuset allows instead.");
            }

            RequestWidening(_selection, restored);
        }
    }

    /// <summary>
    /// A thread started from inside the scope inherits the narrowed mask: the thread pool injects its workers on the
    /// thread that queues the work, and processing queues plenty. Every scope asks for a scan that puts them back, so
    /// the scope narrows its own thread alone, and the scan runs on the pool, off the processing thread, which is by
    /// then restored and so starts no inheritor of its own. A thread an inheritor starts after the scan is put back by
    /// the next scope's scan.
    /// </summary>
    /// <remarks>
    /// The scan widens any thread whose mask equals the selection, not only the ones created in the scope. Nothing
    /// else pins threads today; a later split that pins prewarm workers to core types needs its own masks to differ.
    /// </remarks>
    private static void RequestWidening(Selection from, in CpuMask to)
    {
        _widenFrom = from;
        _widenTo = to;
        Volatile.Write(ref _scanRequested, 1);
        if (Interlocked.CompareExchange(ref _scanRunning, 1, 0) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(static _ => RunWidening(), null);
    }

    private static void RunWidening()
    {
        do
        {
            while (Interlocked.Exchange(ref _scanRequested, 0) == 1)
            {
                try
                {
                    if (_widenFrom is { } from) WidenInheritors(EnumerateThreads(), from.Mask, _widenTo, sched_getaffinity, sched_setaffinity);
                }
                // An unforeseen failure leaves a thread narrowed, which the next scope's scan retries; the pool thread survives.
                catch (Exception)
                {
                }
            }

            Volatile.Write(ref _scanRunning, 0);
        }
        // A request made after the loop drained and before the flag cleared would otherwise wait for the next scope.
        while (Volatile.Read(ref _scanRequested) == 1 && Interlocked.CompareExchange(ref _scanRunning, 1, 0) == 0);
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

            try
            {
                string? performanceCpus = ReadOrNull("/sys/devices/cpu_core/cpus");
                if (performanceCpus is null || !TryReadAffinity(out CpuMask allowedMask)) return selections;

                HashSet<int> allowed = [];
                for (int cpu = 0; cpu < MaxCpus; cpu++)
                {
                    if (allowedMask.Contains(cpu)) allowed.Add(cpu);
                }

                foreach (ProcessingCores cores in Enum.GetValues<ProcessingCores>())
                {
                    if (TryBuildMask(cores, performanceCpus, allowed, ReadSiblings, out CpuMask mask, out int[] cpus))
                        selections[(int)cores] = new Selection(mask, cpus);
                }

                return selections;
            }
            // An opt-in knob must not fail startup, nor poison the type for the processing loop: anything unforeseen narrows nothing.
            catch (Exception)
            {
                return new Selection?[ModeCount];
            }
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
    /// <param name="allowed">The CPUs the calling thread may run on, null or empty when unknown.</param>
    /// <param name="siblingsOf">A logical processor's hyperthread siblings as a Linux CPU list, null when unknown.</param>
    /// <param name="mask">The logical processors to run on.</param>
    /// <param name="cpus">The same, as a sorted list.</param>
    /// <returns>
    /// <c>false</c> for <see cref="ProcessingCores.All"/>, on a CPU with one kind of core, and where the selection
    /// holds every CPU the process may run on or none of them, since there is then nothing to narrow. Also <c>false</c>
    /// for <see cref="ProcessingCores.PerformancePhysical"/> when a core's hyperthreads are unknown: it would otherwise
    /// narrow to what <see cref="ProcessingCores.Performance"/> does, and the two exist to be compared.
    /// </returns>
    internal static bool TryBuildMask(ProcessingCores cores, string? performanceCpus, HashSet<int>? allowed, Func<int, string?> siblingsOf, out CpuMask mask, out int[] cpus)
    {
        mask = default;
        cpus = [];
        if (cores == ProcessingCores.All || performanceCpus is null) return false;

        HashSet<int> selected = ParseCpuList(performanceCpus);
        int allowedCount = int.MaxValue;
        if (allowed is { Count: > 0 })
        {
            selected.IntersectWith(allowed);
            allowedCount = allowed.Count;
        }

        if (cores == ProcessingCores.PerformancePhysical)
        {
            // The lowest selected hyperthread of each core stands for the core.
            HashSet<int> performance = [.. selected];
            foreach (int cpu in performance)
            {
                if (siblingsOf(cpu) is not { } siblings) return false;

                foreach (int sibling in ParseCpuList(siblings))
                {
                    if (sibling < cpu && performance.Contains(sibling))
                    {
                        selected.Remove(cpu);
                        break;
                    }
                }
            }
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

    /// <summary>Puts every thread in <paramref name="tids"/> that still holds <paramref name="narrowed"/> back on <paramref name="target"/>.</summary>
    /// <returns>How many threads it put back.</returns>
    internal static int WidenInheritors(IEnumerable<int> tids, in CpuMask narrowed, CpuMask target, GetAffinity getAffinity, SetAffinity setAffinity)
    {
        int widened = 0;
        foreach (int tid in tids)
        {
            // A thread that exited meanwhile fails the calls and is skipped.
            if (getAffinity(tid, out CpuMask current) == 0 && current.SequenceEqual(narrowed) && setAffinity(tid, ref target) == 0)
                widened++;
        }

        return widened;
    }

    private static IEnumerable<int> EnumerateThreads()
    {
        IEnumerable<string> tasks;
        try
        {
            tasks = Directory.EnumerateDirectories("/proc/self/task");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string task in tasks)
        {
            if (int.TryParse(Path.GetFileName(task.AsSpan()), out int tid)) yield return tid;
        }
    }

    /// <summary>
    /// The calling thread's allowed CPUs, when they can be read here: the call can be missing from the C library, and
    /// the processing loop must not find that out block by block. A kernel that refuses the write makes the scope a no-op.
    /// </summary>
    private static bool TryReadAffinity(out CpuMask mask)
    {
        try
        {
            return sched_getaffinity(0, CpuMaskSize, out mask) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            mask = default;
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

    private static int sched_getaffinity(int tid, out CpuMask mask) => sched_getaffinity(tid, CpuMaskSize, out mask);

    private static int sched_setaffinity(int tid, ref CpuMask mask) => sched_setaffinity(tid, CpuMaskSize, ref mask);

    [DllImport("libc")]
    private static extern int sched_getaffinity(int pid, nint cpusetsize, out CpuMask mask);

    [DllImport("libc")]
    private static extern int sched_setaffinity(int pid, nint cpusetsize, ref CpuMask mask);
}
