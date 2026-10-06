// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.Core;

public static class ApicalProbe
{
    private const int Words = 16;
    private const int SweepIntervalMs = 250;
    private static readonly nint MaskSize = Words * sizeof(ulong);
    private static readonly int Reserve = ParseEnv("APICAL_RESERVE");
    private static readonly int RocksNice = ParseEnv("APICAL_ROCKS_NICE");
    public static readonly bool Enabled = OperatingSystem.IsLinux() && (Reserve > 0 || RocksNice > 0);

    private static readonly HashSet<int> Moved = [];
    private static readonly HashSet<int> Niced = [];
    private static ulong[]? _mainMask;
    private static ulong[]? _helperMask;
    private static volatile int _mainTid;
    private static int _started;

    public static void BeginBlock()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) Start();
        if (_mainMask is null) return;
        _mainTid = CurrentTid();
        sched_setaffinity(0, MaskSize, _mainMask);
    }

    public static void EndBlock()
    {
        if (_helperMask is null) return;
        sched_setaffinity(0, MaskSize, _helperMask);
        _mainTid = 0;
    }

    private static void Start()
    {
        string mainCpus = "-";
        string helperCpus = "-";
        if (Reserve > 0) BuildMasks(out mainCpus, out helperCpus);
        Console.WriteLine($"APICAL reserve={Reserve} rocks_nice={RocksNice} main_cpus={mainCpus} helper_cpus={helperCpus}");
        Sweep();
        Thread sweeper = new(SweepLoop) { IsBackground = true, Name = "ApicalSweeper" };
        sweeper.Start();
    }

    private static void BuildMasks(out string mainCpus, out string helperCpus)
    {
        mainCpus = "-";
        helperCpus = "-";
        ulong[] allowed = new ulong[Words];
        if (sched_getaffinity(0, MaskSize, allowed) != 0) return;
        List<int> cpus = [];
        for (int cpu = 0; cpu < Words * 64; cpu++)
        {
            if (Has(allowed, cpu)) cpus.Add(cpu);
        }
        if (cpus.Count < 4) return;

        int apex = cpus[^1];
        List<int> main = [apex];
        if (Reserve > 1)
        {
            foreach (int sibling in Siblings(apex))
            {
                if (sibling != apex && Has(allowed, sibling)) main.Add(sibling);
            }
        }

        ulong[] mainMask = new ulong[Words];
        ulong[] helperMask = new ulong[Words];
        List<int> helpers = [];
        foreach (int cpu in cpus)
        {
            if (main.Contains(cpu))
            {
                Set(mainMask, cpu);
            }
            else
            {
                Set(helperMask, cpu);
                helpers.Add(cpu);
            }
        }
        if (helpers.Count == 0) return;

        mainCpus = string.Join('+', main);
        helperCpus = string.Join('+', helpers);
        _helperMask = helperMask;
        _mainMask = mainMask;
    }

    private static void SweepLoop()
    {
        while (true)
        {
            Thread.Sleep(SweepIntervalMs);
            try
            {
                Sweep();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Sweep()
    {
        ulong[]? helperMask = _helperMask;
        foreach (string dir in Directory.EnumerateDirectories("/proc/self/task"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int tid)) continue;
            bool needsMove = helperMask is not null && !Moved.Contains(tid);
            bool needsNice = RocksNice > 0 && !Niced.Contains(tid);
            if (!needsMove && !needsNice) continue;
            if (tid == _mainTid) continue;

            string comm;
            try
            {
                comm = File.ReadAllText(dir + "/comm").TrimEnd();
            }
            catch (IOException)
            {
                continue;
            }

            if (needsNice)
            {
                Niced.Add(tid);
                if (comm.StartsWith("rocksdb:low", StringComparison.Ordinal)) setpriority(0, tid, RocksNice);
            }

            if (needsMove)
            {
                Moved.Add(tid);
                if (!comm.StartsWith(".NET Server GC", StringComparison.Ordinal)) sched_setaffinity(tid, MaskSize, helperMask!);
            }
        }
    }

    private static int CurrentTid()
    {
        string? target = new FileInfo("/proc/thread-self").LinkTarget;
        if (target is null) return 0;
        return int.TryParse(target[(target.LastIndexOf('/') + 1)..], out int tid) ? tid : 0;
    }

    private static IEnumerable<int> Siblings(int cpu)
    {
        string text;
        try
        {
            text = File.ReadAllText($"/sys/devices/system/cpu/cpu{cpu}/topology/thread_siblings_list").Trim();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] range = part.Split('-');
            if (!int.TryParse(range[0], out int from)) continue;
            int to = from;
            if (range.Length > 1 && !int.TryParse(range[1], out to)) continue;
            for (int sibling = from; sibling <= to; sibling++) yield return sibling;
        }
    }

    private static bool Has(ulong[] mask, int cpu) => (mask[cpu >> 6] & (1UL << (cpu & 63))) != 0;

    private static void Set(ulong[] mask, int cpu) => mask[cpu >> 6] |= 1UL << (cpu & 63);

    private static int ParseEnv(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : 0;

    [DllImport("libc")]
    private static extern int sched_getaffinity(int pid, nint cpusetsize, [Out] ulong[] mask);

    [DllImport("libc")]
    private static extern int sched_setaffinity(int pid, nint cpusetsize, ulong[] mask);

    [DllImport("libc")]
    private static extern int setpriority(int which, int who, int prio);
}
