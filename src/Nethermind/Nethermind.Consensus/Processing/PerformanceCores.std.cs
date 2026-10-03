// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
/// it refuses falls back to every CPU the cpuset allows. A thread started from a narrowed thread does not keep its
/// mask: the runtime resets every new managed thread to the process's mask as it starts.
/// </remarks>
internal static partial class PerformanceCores
{
    /// <summary>The CPUs a Linux <c>cpu_set_t</c> holds.</summary>
    internal const int MaxCpus = MaskWords * 64;
    private const int MaskWords = 16;
    private static readonly nint CpuMaskSize = MaskWords * sizeof(ulong);
    private static readonly int ModeCount = Enum.GetValues<ProcessingCores>().Length;

    private static int _restoreRefused;

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

        public readonly bool Overlaps(in CpuMask other)
        {
            for (int i = 0; i < MaskWords; i++)
            {
                if ((this[i] & other[i]) != 0) return true;
            }

            return false;
        }
    }

    // The thread narrowed onto the dedicated core, which the guard leaves there; 0 while none is.
    private static int _processingThreadId;

    /// <summary>The logical processors <paramref name="cores"/> narrows the processing thread to; empty where it narrows nothing.</summary>
    public static ReadOnlySpan<int> Cpus(ProcessingCores cores) => Selected(cores)?.Cpus ?? [];

    /// <summary>
    /// Keeps the calling thread on the logical processors <paramref name="cores"/> selects until the scope is disposed,
    /// on the same thread. Does nothing where that narrows nothing, and where the kernel refuses.
    /// </summary>
    /// <remarks>
    /// With <see cref="ProcessingCores.Dedicated"/>, the first call also starts <see cref="DedicatedCoreGuard"/>, which
    /// keeps the process's other threads off that core.
    /// </remarks>
    public static Scope NarrowCurrentThread(ProcessingCores cores, ILogger logger)
    {
        if (Selected(cores) is not { } selection) return default;
        if (cores != ProcessingCores.Dedicated || Host.OffDedicated is not { } others || !TryGetCurrentThreadId(out int threadId))
            return Narrow(selection, logger);

        // Registered before narrowing, so the guard never moves this thread off the core it is about to take.
        Volatile.Write(ref _processingThreadId, threadId);
        DedicatedCoreGuard.EnsureStarted(others.Mask, selection.Mask);
        Scope scope = Narrow(selection, logger, processingThread: true);
        if (!scope.Narrowed) Volatile.Write(ref _processingThreadId, 0);
        return scope;
    }

    /// <summary>
    /// How prewarm divides its workers between the core types: the first <see cref="PrewarmSplit.NearWorkers"/> run
    /// on the performance cores and warm what the processing thread reaches next, the rest on the efficiency cores and
    /// warm the far end of the block. Null on a CPU with one kind of core, or a cpuset holding only one kind.
    /// </summary>
    public static PrewarmSplit? PrewarmFor(ProcessingCores cores) =>
        cores == ProcessingCores.All || (uint)cores >= (uint)ModeCount || !OperatingSystem.IsLinux() ? null
        : cores == ProcessingCores.Dedicated ? Host.PrewarmDedicated
        : Host.Prewarm;

    internal sealed class PrewarmSplit(Selection near, Selection far, int nearWorkers)
    {
        public Selection Near { get; } = near;
        public Selection Far { get; } = far;

        /// <summary>How many workers run on <see cref="Near"/>: one per logical processor the processing thread leaves.</summary>
        public int NearWorkers { get; } = Math.Max(1, nearWorkers);

        public Scope NarrowNear(ILogger logger) => Narrow(Near, logger);

        public Scope NarrowFar(ILogger logger) => Narrow(Far, logger);
    }

    private static Scope Narrow(Selection selection, ILogger logger, bool processingThread = false)
    {
        // pid 0 is the calling thread.
        if (sched_getaffinity(0, CpuMaskSize, out CpuMask previous) != 0) return default;
        CpuMask mask = selection.Mask;
        return sched_setaffinity(0, CpuMaskSize, ref mask) == 0 ? new Scope(previous, logger, processingThread) : default;
    }

    public readonly struct Scope : IDisposable
    {
        private readonly bool _narrowed;
        private readonly bool _registered;
        private readonly CpuMask _previous;
        private readonly ILogger _logger;

        internal Scope(CpuMask previous, ILogger logger, bool processingThread)
        {
            _narrowed = true;
            _registered = processingThread;
            _previous = previous;
            _logger = logger;
        }

        internal bool Narrowed => _narrowed;

        public void Dispose()
        {
            if (!_narrowed) return;

            CpuMask restored = _previous;
            if (sched_setaffinity(0, CpuMaskSize, ref restored) != 0)
            {
                // A cpuset changed at runtime to exclude the previous mask; the kernel narrows every CPU to the new cpuset.
                restored = CpuMask.Every();
                sched_setaffinity(0, CpuMaskSize, ref restored);
                if (Interlocked.Exchange(ref _restoreRefused, 1) == 0 && _logger.IsWarn)
                    _logger.Warn("A block processing thread could not restore its CPU affinity, so it was widened to every CPU the cpuset allows instead.");
            }

            if (_registered) Volatile.Write(ref _processingThreadId, 0);
        }
    }

    /// <summary>
    /// Keeps every thread of the process but the processing one off the dedicated core. The scheduler would otherwise run
    /// pool threads there, and the processing loop, narrowed onto the core each time it resumes, would wait for them.
    /// </summary>
    /// <remarks>
    /// The runtime starts each new thread on the process's full mask, so one pass is not enough: a background thread
    /// checks every thread each <see cref="Interval"/> and narrows those that may run on the dedicated core to the
    /// others. A thread that exits mid-pass, or a mask the kernel refuses, is tried again on the next pass.
    /// </remarks>
    private static class DedicatedCoreGuard
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
        private static int _started;

        public static void EnsureStarted(CpuMask others, CpuMask dedicated)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            new Thread(() => Run(others, dedicated)) { IsBackground = true, Name = "Dedicated core guard" }.Start();
        }

        private static void Run(CpuMask others, CpuMask dedicated)
        {
            while (true)
            {
                try
                {
                    MoveOffDedicatedCore(ThreadIds(), others, dedicated, keep: 0);
                }
                catch (Exception)
                {
                    // /proc unreadable for a moment: the next pass tries again.
                }

                Thread.Sleep(Interval);
            }
        }

        private static IEnumerable<int> ThreadIds()
        {
            foreach (string task in Directory.EnumerateDirectories("/proc/self/task"))
            {
                if (int.TryParse(Path.GetFileName(task), out int threadId)) yield return threadId;
            }
        }
    }

    /// <summary>
    /// Narrows each of <paramref name="threads"/> that may run on <paramref name="dedicated"/> to <paramref name="others"/>,
    /// except <paramref name="keep"/> and the registered processing thread.
    /// </summary>
    /// <returns>How many threads were narrowed.</returns>
    internal static int MoveOffDedicatedCore(IEnumerable<int> threads, CpuMask others, CpuMask dedicated, int keep)
    {
        int moved = 0;
        foreach (int threadId in threads)
        {
            if (threadId == keep || threadId == Volatile.Read(ref _processingThreadId)) continue;
            if (!TryGetAffinity(threadId, out CpuMask current) || !current.Overlaps(dedicated)) continue;
            // Read again: the processing loop may have registered this thread since the check above.
            if (threadId == Volatile.Read(ref _processingThreadId)) continue;
            if (sched_setaffinity(threadId, CpuMaskSize, ref others) == 0) moved++;
        }

        return moved;
    }

    internal static bool TryGetAffinity(int threadId, out CpuMask mask) => sched_getaffinity(threadId, CpuMaskSize, out mask) == 0;

    /// <summary>The kernel's id of the calling thread; false where the C library does not export <c>gettid</c>.</summary>
    internal static bool TryGetCurrentThreadId(out int threadId)
    {
        try
        {
            threadId = gettid();
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            threadId = 0;
            return false;
        }
    }

    internal sealed class Selection(CpuMask mask, int[] cpus)
    {
        public CpuMask Mask { get; } = mask;
        public int[] Cpus { get; } = cpus;
    }

    /// <summary>Built on first use by a mode that narrows, so <see cref="ProcessingCores.All"/> alone leaves the host untouched.</summary>
    private static class Host
    {
        public static readonly Selection?[] Selections;
        public static readonly PrewarmSplit? Prewarm;
        public static readonly PrewarmSplit? PrewarmDedicated;
        /// <summary>Every allowed logical processor but the dedicated core's: where the guard keeps the other threads.</summary>
        public static readonly Selection? OffDedicated;

        static Host()
        {
            Selections = new Selection?[ModeCount];
            try
            {
                string? performanceCpus = ReadOrNull("/sys/devices/cpu_core/cpus");
                if (!TryReadAffinity(out CpuMask allowedMask)) return;

                HashSet<int> allowed = [];
                for (int cpu = 0; cpu < MaxCpus; cpu++)
                {
                    if (allowedMask.Contains(cpu)) allowed.Add(cpu);
                }

                if (performanceCpus is null)
                {
                    // One kind of core: only Dedicated narrows anything.
                    if (TryBuildUniformDedicated(allowed, ReadSiblings, out Selection? uniformCore, out PrewarmSplit? uniformPrewarm))
                    {
                        Selections[(int)ProcessingCores.Dedicated] = uniformCore;
                        PrewarmDedicated = uniformPrewarm;
                        OffDedicated = uniformPrewarm.Near;
                    }

                    return;
                }

                foreach (ProcessingCores cores in Enum.GetValues<ProcessingCores>())
                {
                    if (TryBuildMask(cores, performanceCpus, allowed, ReadSiblings, out CpuMask mask, out int[] cpus))
                        Selections[(int)cores] = new Selection(mask, cpus);
                }

                int[] every = [.. allowed];
                Array.Sort(every);
                if (Selections[(int)ProcessingCores.Dedicated] is { } dedicatedCore
                    && TryExclude(every, dedicatedCore.Cpus, out CpuMask othersMask, out int[] otherCpus))
                {
                    OffDedicated = new Selection(othersMask, otherCpus);
                }

                if (Selections[(int)ProcessingCores.Performance] is { } near
                    && TryBuildEfficiencyMask(performanceCpus, allowed, out CpuMask farMask, out int[] farCpus))
                {
                    Selection far = new(farMask, farCpus);
                    // The processing thread shares the performance cores with the near workers, so they leave it one.
                    Prewarm = new PrewarmSplit(near, far, near.Cpus.Length - 1);

                    PrewarmDedicated = Prewarm;
                    if (Selections[(int)ProcessingCores.Dedicated] is { } dedicated
                        && TryExclude(near.Cpus, dedicated.Cpus, out CpuMask rest, out int[] restCpus))
                    {
                        // The processing thread has its core to itself, so the near workers take every other one.
                        PrewarmDedicated = new PrewarmSplit(new Selection(rest, restCpus), far, restCpus.Length);
                    }
                }
            }
            // An opt-in knob must not fail startup, nor poison the type for the processing loop: anything unforeseen narrows nothing.
            catch (Exception)
            {
                Selections = new Selection?[ModeCount];
                Prewarm = null;
                PrewarmDedicated = null;
                OffDedicated = null;
            }
        }
    }

    /// <summary>
    /// <see cref="ProcessingCores.Dedicated"/> on a CPU with one kind of core: every core counts as a performance core, the
    /// processing thread gets one to itself, and every prewarm worker runs on the others.
    /// </summary>
    /// <remarks>
    /// Without it, the processing thread shares a core with a prewarm worker whenever prewarming keeps every logical
    /// processor busy, and the two hyperthreads then split the core's execution units.
    /// </remarks>
    internal static bool TryBuildUniformDedicated(HashSet<int> allowed, Func<int, string?> siblingsOf,
        [NotNullWhen(true)] out Selection? dedicated, [NotNullWhen(true)] out PrewarmSplit? prewarm)
    {
        dedicated = null;
        prewarm = null;
        if (allowed.Count == 0) return false;

        int[] every = [.. allowed];
        Array.Sort(every);
        if (!TryBuildMask(ProcessingCores.Dedicated, string.Join(",", every), allowed, siblingsOf, out CpuMask mask, out int[] cpus)
            || !TryExclude(every, cpus, out CpuMask restMask, out int[] restCpus))
        {
            return false;
        }

        dedicated = new Selection(mask, cpus);
        Selection rest = new(restMask, restCpus);
        prewarm = new PrewarmSplit(rest, rest, restCpus.Length);
        return true;
    }

    // Any value the enum does not name - the config binder accepts numbers - narrows nothing.
    private static Selection? Selected(ProcessingCores cores) =>
        cores != ProcessingCores.All && (uint)cores < (uint)ModeCount && OperatingSystem.IsLinux() ? Host.Selections[(int)cores] : null;

    /// <summary>
    /// The logical processors of the first performance core that does not hold CPU 0, which takes more interrupts; the
    /// core of CPU 0 only when it is the one allowed. Null when a core's hyperthreads are unknown, since the core could
    /// then not be kept whole or clear of CPU 0.
    /// </summary>
    /// <remarks>
    /// Chosen by position rather than by speed: the top frequency and the CPPC level barely differ between the
    /// performance cores, so ranking by them would pick an effectively random core.
    /// </remarks>
    private static HashSet<int>? DedicatedCore(HashSet<int> performance, Func<int, string?> siblingsOf)
    {
        int[] ordered = [.. performance];
        Array.Sort(ordered);
        HashSet<int>? first = null;
        foreach (int cpu in ordered)
        {
            if (siblingsOf(cpu) is not { } siblings) return null;

            HashSet<int> core = [cpu];
            foreach (int sibling in ParseCpuList(siblings))
            {
                if (performance.Contains(sibling)) core.Add(sibling);
            }

            if (!core.Contains(0) && !ParseCpuList(siblings).Contains(0)) return core;
            first ??= core;
        }

        return first;
    }

    /// <summary><paramref name="cpus"/> without <paramref name="excluded"/>, when anything is left.</summary>
    internal static bool TryExclude(int[] cpus, int[] excluded, out CpuMask mask, out int[] rest)
    {
        mask = default;
        List<int> kept = [];
        foreach (int cpu in cpus)
        {
            if (Array.IndexOf(excluded, cpu) < 0) kept.Add(cpu);
        }

        rest = [.. kept];
        foreach (int cpu in rest) mask.Add(cpu);
        return rest.Length > 0 && rest.Length < cpus.Length;
    }

    /// <summary>
    /// The CPUs the process may run on that are not performance cores, when there are both kinds among them; on an
    /// Intel hybrid CPU, the efficiency cores.
    /// </summary>
    internal static bool TryBuildEfficiencyMask(string? performanceCpus, HashSet<int>? allowed, out CpuMask mask, out int[] cpus)
    {
        mask = default;
        cpus = [];
        if (performanceCpus is null || allowed is not { Count: > 0 }) return false;

        HashSet<int> performance = ParseCpuList(performanceCpus);
        List<int> efficiency = [];
        bool anyPerformance = false;
        foreach (int cpu in allowed)
        {
            if (performance.Contains(cpu)) anyPerformance = true;
            else efficiency.Add(cpu);
        }

        if (!anyPerformance || efficiency.Count == 0) return false;

        cpus = [.. efficiency];
        Array.Sort(cpus);
        foreach (int cpu in cpus) mask.Add(cpu);
        return true;
    }

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
    /// narrow to what <see cref="ProcessingCores.Performance"/> does, and the two exist to be compared; and for
    /// <see cref="ProcessingCores.Dedicated"/>, which could then not keep the core whole.
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

        if (cores == ProcessingCores.Dedicated)
        {
            if (DedicatedCore(selected, siblingsOf) is not { } core) return false;
            selected = core;
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

    [DllImport("libc")]
    private static extern int sched_getaffinity(int pid, nint cpusetsize, out CpuMask mask);

    [DllImport("libc")]
    private static extern int sched_setaffinity(int pid, nint cpusetsize, ref CpuMask mask);

    [DllImport("libc")]
    private static extern int gettid();
}
