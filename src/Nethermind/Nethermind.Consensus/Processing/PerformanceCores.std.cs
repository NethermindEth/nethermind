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
/// CPU selections and affinity scopes for processing, prewarming, and background workers.
/// </summary>
/// <remarks>
/// The processing loop continues on whichever thread-pool thread resumes it, and on a CPU with performance and
/// efficiency cores the scheduler can start that thread on an efficiency core for a block that lasts tens of
/// milliseconds. Hybrid core types come from Linux's Intel hybrid PMU listing (<c>/sys/devices/cpu_core/cpus</c>)
/// or Windows CPU Sets. Dedicated Linux workers also use per-CPU capacity when available. The allowed CPUs are read once, so a cpuset
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
    private static int _restoreRefused;

    // The threads a scope holds narrowed right now, by kernel thread id, and the mask each wants. A slot holds the
    // negated id while its mask is written and the id once it is published, and it is published before the thread
    // narrows itself; it is freed before the thread is restored. The scan leaves these threads alone, and puts back
    // the mask of one that entered its scope while the scan was widening it.
    private const int MaxScopes = 128;
    private static readonly int[] _scoped = new int[MaxScopes];
    private static readonly CpuMask[] _scopedMasks = new CpuMask[MaxScopes];
    // Bumped on every publish, so a scan copying a mask sees if the slot was freed and taken again meanwhile.
    private static readonly int[] _scopedVersions = new int[MaxScopes];

    internal enum ScopeState { None, Entering, Narrowed }

    internal delegate ScopeState ScopeOf(int tid, out CpuMask wanted);

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
    public static Scope NarrowCurrentThread(ProcessingCores cores, ILogger logger) =>
        Selected(cores) is { } selection ? Narrow(selection, logger, widenOnDispose: true) : default;

    /// <summary>
    /// How prewarm divides its workers between the core types: the first <see cref="PrewarmSplit.NearWorkers"/> run
    /// on the performance cores and warm what the processing thread reaches next, the rest on the efficiency cores and
    /// warm the far end of the block. Null on a CPU with one kind of core, or a cpuset holding only one kind.
    /// </summary>
    public static PrewarmSplit? PrewarmFor(ProcessingCores cores) =>
        cores == ProcessingCores.All || (uint)cores >= (uint)ModeCount || !(OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) ? null
        : cores == ProcessingCores.Fastest ? Host.PrewarmFastest
        : Host.Prewarm;

    internal sealed class PrewarmSplit(Selection near, Selection far, int nearWorkers)
    {
        public Selection Near { get; } = near;
        public Selection Far { get; } = far;

        /// <summary>How many workers run on <see cref="Near"/>: one per logical processor the processing thread leaves.</summary>
        public int NearWorkers { get; } = Math.Max(1, nearWorkers);

        public Scope NarrowNear(ILogger logger) => Narrow(Near, logger, widenOnDispose: false);

        public Scope NarrowFar(ILogger logger) => Narrow(Far, logger, widenOnDispose: false);
    }

    private static Scope Narrow(Selection selection, ILogger logger, bool widenOnDispose)
    {
        if (OperatingSystem.IsWindows()) return NarrowWindows(selection, logger);
        // pid 0 is the calling thread.
        if (sched_getaffinity(0, CpuMaskSize, out CpuMask previous) != 0) return default;
        // A nested scope must restore its caller's mask, including a dedicated worker's pin.
        if (ScopeOfThread(CurrentThreadId(), out _) == ScopeState.None) previous = RestoreTarget(previous, Host.Narrowed, Host.Allowed);
        CpuMask mask = selection.Mask;
        if (!TryTakeSlot(mask, out int slot)) return default;

        if (sched_setaffinity(0, CpuMaskSize, ref mask) == 0) return new Scope(slot, previous, logger, widenOnDispose);

        Volatile.Write(ref _scoped[slot], 0);
        return default;
    }

    private static bool TryTakeSlot(in CpuMask mask, out int slot)
    {
        int tid = CurrentThreadId();
        if (tid > 0)
        {
            for (slot = 0; slot < MaxScopes; slot++)
            {
                if (Volatile.Read(ref _scoped[slot]) != 0 || Interlocked.CompareExchange(ref _scoped[slot], -tid, 0) != 0) continue;

                _scopedMasks[slot] = mask;
                Interlocked.Increment(ref _scopedVersions[slot]);
                Volatile.Write(ref _scoped[slot], tid);
                return true;
            }
        }

        // More scopes than slots, or no thread id: narrowing a thread the scan cannot see would let it widen that
        // thread mid-scope, so this one narrows nothing.
        slot = -1;
        return false;
    }

    public readonly struct Scope : IDisposable
    {
        // One past the slot, so the default scope holds none.
        private readonly int _slot;
        private readonly CpuMask _previous;
        private readonly ILogger _logger;
        private readonly bool _widenOnDispose;
        private readonly uint[]? _previousCpuSets;

        internal Scope(int slot, CpuMask previous, ILogger logger, bool widenOnDispose)
        {
            _slot = slot + 1;
            _previous = previous;
            _logger = logger;
            _widenOnDispose = widenOnDispose;
        }

        internal Scope(uint[] previousCpuSets, ILogger logger)
        {
            _previousCpuSets = previousCpuSets;
            _logger = logger;
        }

        public void Dispose()
        {
            if (_previousCpuSets is { } previous)
            {
                if (!SetThreadSelectedCpuSets(GetCurrentThread(), previous, (uint)previous.Length) && _logger.IsWarn)
                    _logger.Warn("Could not restore the worker's Windows CPU Sets.");
                return;
            }
            if (_slot == 0) return;

            // Freed first: a scan that finds the thread still narrowed after this widens it, which is where it is going.
            Volatile.Write(ref _scoped[_slot - 1], 0);
            CpuMask restored = _previous;
            if (sched_setaffinity(0, CpuMaskSize, ref restored) != 0)
            {
                // A cpuset changed at runtime to exclude the previous mask; the kernel narrows every CPU to the new cpuset.
                restored = CpuMask.Every();
                sched_setaffinity(0, CpuMaskSize, ref restored);
                if (Interlocked.Exchange(ref _restoreRefused, 1) == 0 && _logger.IsWarn)
                    _logger.Warn("A block processing thread could not restore its CPU affinity, so it was widened to every CPU the cpuset allows instead.");
            }

            if (_widenOnDispose) RequestWidening();
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
    /// The scan widens any thread outside a scope whose mask equals one this class narrows to, not only the ones
    /// created in a scope; nothing else in the process pins threads. Threads inside a scope are skipped, so one scope
    /// ending never widens another that is still open.
    /// </remarks>
    private static void RequestWidening()
    {
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
                    // Once the kernel has refused a restore the recorded allowed set is stale; every CPU is narrowed to the cpuset.
                    CpuMask target = Volatile.Read(ref _restoreRefused) == 0 ? Host.Allowed : CpuMask.Every();
                    WidenInheritors(EnumerateThreads(), Host.Narrowed, target, ScopeOfThread, sched_getaffinity, sched_setaffinity);
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
        public uint[]? CpuSets { get; init; }
    }

    /// <summary>Built on first use by a mode that narrows, so <see cref="ProcessingCores.All"/> alone leaves the host untouched.</summary>
    private static class Host
    {
        public static readonly Selection?[] Selections;
        public static readonly PrewarmSplit? Prewarm;
        public static readonly PrewarmSplit? PrewarmFastest;
        public static readonly Selection? DedicatedProcessing;
        public static readonly Selection? DedicatedBackground;

        /// <summary>Every mask this class narrows a thread to, which is what the widening scan looks for.</summary>
        public static readonly CpuMask[] Narrowed;

        /// <summary>What the process may run on, read once: the one mask a thread is put back on.</summary>
        public static readonly CpuMask Allowed;

        static Host()
        {
            Selections = new Selection?[ModeCount];
            Narrowed = [];
            try
            {
                List<Cpu> cpus = ReadCpus();
                DedicatedProcessing = DedicatedSelection(cpus, background: false);
                DedicatedBackground = DedicatedSelection(cpus, background: true);
                if (OperatingSystem.IsWindows())
                {
                    BuildWindowsSelections(cpus, Selections, out Prewarm);
                    PrewarmFastest = Prewarm;
                    return;
                }
                string? performanceCpus = ReadOrNull("/sys/devices/cpu_core/cpus");
                // Without a thread id the scan could not tell a scope's thread from an inheritor, so nothing narrows.
                if (performanceCpus is null || CurrentThreadId() <= 0 || !TryReadAffinity(out CpuMask allowedMask)) return;
                Allowed = allowedMask;

                HashSet<int> allowed = [];
                for (int cpu = 0; cpu < MaxCpus; cpu++)
                {
                    if (allowedMask.Contains(cpu)) allowed.Add(cpu);
                }

                List<CpuMask> narrowed = [];
                foreach (ProcessingCores cores in Enum.GetValues<ProcessingCores>())
                {
                    if (!TryBuildMask(cores, performanceCpus, allowed, ReadSiblings, out CpuMask mask, out int[] selectedCpus, ReadTopPerformance)) continue;
                    Selections[(int)cores] = new Selection(mask, selectedCpus);
                    narrowed.Add(mask);
                }

                if (Selections[(int)ProcessingCores.Performance] is { } near
                    && TryBuildEfficiencyMask(performanceCpus, allowed, out CpuMask farMask, out int[] farCpus))
                {
                    Selection far = new(farMask, farCpus);
                    // The processing thread shares the performance cores with the near workers, so they leave it one.
                    Prewarm = new PrewarmSplit(near, far, near.Cpus.Length - 1);
                    narrowed.Add(farMask);

                    PrewarmFastest = Prewarm;
                    if (Selections[(int)ProcessingCores.Fastest] is { } fastest
                        && TryExclude(near.Cpus, fastest.Cpus, out CpuMask rest, out int[] restCpus))
                    {
                        // The processing thread has its core to itself, so the near workers take every other one.
                        PrewarmFastest = new PrewarmSplit(new Selection(rest, restCpus), far, restCpus.Length);
                        narrowed.Add(rest);
                    }
                }

                Narrowed = [.. narrowed];
            }
            // An opt-in knob must not fail startup, nor poison the type for the processing loop: anything unforeseen narrows nothing.
            catch (Exception)
            {
                Selections = new Selection?[ModeCount];
                Prewarm = null;
                PrewarmFastest = null;
                DedicatedProcessing = null;
                DedicatedBackground = null;
                Narrowed = [];
            }
        }
    }

    // Any value the enum does not name - the config binder accepts numbers - narrows nothing.
    private static Selection? Selected(ProcessingCores cores) =>
        cores != ProcessingCores.All && (uint)cores < (uint)ModeCount && (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) ? Host.Selections[(int)cores] : null;

    /// <summary>
    /// The logical processors of the performance core with the highest top speed, when the cores differ; null when
    /// they all report the same, or when a speed is unknown, and then the caller keeps every performance core.
    /// </summary>
    private static HashSet<int>? FastestCore(HashSet<int> performance, Func<int, string?> siblingsOf, Func<int, long?>? topPerformanceOf)
    {
        if (topPerformanceOf is null || performance.Count == 0) return null;

        int fastest = -1;
        long best = long.MinValue;
        bool differ = false;
        foreach (int cpu in performance)
        {
            if (topPerformanceOf(cpu) is not { } speed) return null;
            if (fastest >= 0 && speed != best) differ = true;
            // Ties go to the lowest CPU, so the choice does not depend on the set's order.
            if (speed > best || (speed == best && cpu < fastest))
            {
                best = speed;
                fastest = cpu;
            }
        }

        if (!differ) return null;

        HashSet<int> core = [fastest];
        if (siblingsOf(fastest) is { } siblings)
        {
            foreach (int sibling in ParseCpuList(siblings))
            {
                if (performance.Contains(sibling)) core.Add(sibling);
            }
        }

        return core;
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
    /// narrow to what <see cref="ProcessingCores.Performance"/> does, and the two exist to be compared.
    /// </returns>
    internal static bool TryBuildMask(ProcessingCores cores, string? performanceCpus, HashSet<int>? allowed, Func<int, string?> siblingsOf, out CpuMask mask, out int[] cpus, Func<int, long?>? topPerformanceOf = null)
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

        if (cores == ProcessingCores.Fastest && FastestCore(selected, siblingsOf, topPerformanceOf) is { } core) selected = core;

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
    /// Puts every thread in <paramref name="tids"/> that holds one of the <paramref name="narrowed"/> masks outside a
    /// scope back on <paramref name="target"/>.
    /// </summary>
    /// <returns>How many threads it put back.</returns>
    internal static int WidenInheritors(IEnumerable<int> tids, CpuMask[] narrowed, CpuMask target, ScopeOf scopeOf, GetAffinity getAffinity, SetAffinity setAffinity)
    {
        if (narrowed.Length == 0) return 0;

        int widened = 0;
        foreach (int tid in tids)
        {
            // A thread that exited meanwhile fails the calls and is skipped.
            if (scopeOf(tid, out _) != ScopeState.None || getAffinity(tid, out CpuMask current) != 0 || !IsAnyOf(current, narrowed)) continue;
            CpuMask widenTo = target;
            if (setAffinity(tid, ref widenTo) != 0) continue;

            // The thread entered a scope after it was checked, and may have narrowed itself before the write above; its
            // scope's mask goes back. One still entering narrows itself after publishing, so it overrides this write.
            if (scopeOf(tid, out CpuMask wanted) == ScopeState.Narrowed) setAffinity(tid, ref wanted);
            else widened++;
        }

        return widened;
    }

    /// <summary>
    /// What a scope puts its thread back on. A thread started inside another scope and not yet put back enters holding
    /// that scope's mask; it goes back to what the process may run on, not to that.
    /// </summary>
    internal static CpuMask RestoreTarget(in CpuMask previous, CpuMask[] narrowed, in CpuMask allowed) =>
        IsAnyOf(previous, narrowed) ? allowed : previous;

    private static bool IsAnyOf(in CpuMask mask, CpuMask[] masks)
    {
        foreach (CpuMask candidate in masks)
        {
            if (mask.SequenceEqual(candidate)) return true;
        }

        return false;
    }

    private static ScopeState ScopeOfThread(int tid, out CpuMask wanted)
    {
        for (int slot = 0; slot < MaxScopes; slot++)
        {
            int scoped = Volatile.Read(ref _scoped[slot]);
            if (scoped == -tid)
            {
                wanted = default;
                return ScopeState.Entering;
            }

            if (scoped != tid) continue;
            int version = Volatile.Read(ref _scopedVersions[slot]);
            wanted = _scopedMasks[slot];
            // Freed or taken again while the mask was copied: the copy may mix two masks, so it is not used.
            if (Volatile.Read(ref _scoped[slot]) == tid && Volatile.Read(ref _scopedVersions[slot]) == version) return ScopeState.Narrowed;
        }

        wanted = default;
        return ScopeState.None;
    }

    /// <summary>
    /// The calling thread's kernel id, 0 where it cannot be had. Through <c>syscall</c> rather than <c>gettid</c>, which
    /// glibc exports only from 2.30.
    /// </summary>
    private static int CurrentThreadId() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => (int)syscall(186),
        Architecture.Arm64 => (int)syscall(178),
        _ => 0,
    };

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

    /// <summary>
    /// A logical processor's top speed: the ACPI CPPC highest performance level Linux uses to rank favored cores, or
    /// failing that the top frequency. A host exposes one or the other for all its CPUs.
    /// </summary>
    private static long? ReadTopPerformance(int cpu) =>
        ReadNumber($"/sys/devices/system/cpu/cpu{cpu}/acpi_cppc/highest_perf")
        ?? ReadNumber($"/sys/devices/system/cpu/cpu{cpu}/cpufreq/cpuinfo_max_freq");

    private static long? ReadNumber(string path) => long.TryParse(ReadOrNull(path), out long value) ? value : null;

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
    private static extern long syscall(long number);

    [DllImport("libc")]
    private static extern int sched_getaffinity(int pid, nint cpusetsize, out CpuMask mask);

    [DllImport("libc")]
    private static extern int sched_setaffinity(int pid, nint cpusetsize, ref CpuMask mask);
}
