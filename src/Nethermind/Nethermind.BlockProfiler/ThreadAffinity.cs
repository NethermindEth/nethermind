// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Nethermind.BlockProfiler;

/// <summary>Keeps one CPU for the calling thread through Linux <c>sched_setaffinity</c>.</summary>
internal static unsafe partial class ThreadAffinity
{
    // cpu_set_t is 1024 bits.
    private const int MaskWords = 16;

    // The calling thread's CPU set before its first pin: the CPUs the process may use.
    private static ulong[]? s_processCpus;

    /// <summary>Moves the calling thread onto <paramref name="cpu"/>; false when refused or not on Linux.</summary>
    public static bool PinCurrentThread(int cpu)
    {
        if (!OperatingSystem.IsLinux() || cpu < 0 || cpu >= MaskWords * 64) return false;

        ulong* mask = stackalloc ulong[MaskWords];
        if (s_processCpus is null && sched_getaffinity(0, MaskWords * sizeof(ulong), mask) == 0)
        {
            s_processCpus = new Span<ulong>(mask, MaskWords).ToArray();
        }

        new Span<ulong>(mask, MaskWords).Clear();
        mask[cpu / 64] = 1UL << (cpu % 64);
        // pid 0 is the calling thread; the kernel moves it onto the CPU before returning.
        return sched_setaffinity(0, MaskWords * sizeof(ulong), mask) == 0;
    }

    /// <summary>
    /// Takes <paramref name="cpu"/> out of the CPU set of every other thread of the process, so the pinned thread has
    /// the core to itself. A thread the pinned thread started inherited that one CPU, so it gets the process's other
    /// CPUs instead. Returns how many threads were moved.
    /// </summary>
    public static int ExcludeFromOtherThreads(int cpu)
    {
        if (!OperatingSystem.IsLinux() || cpu < 0 || cpu >= MaskWords * 64) return 0;

        string[] tasks;
        try
        {
            tasks = Directory.GetDirectories("/proc/self/task");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        int self = (int)syscall(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 178 : 186); // gettid
        ulong bit = 1UL << (cpu % 64);
        ulong* mask = stackalloc ulong[MaskWords];
        int moved = 0;
        foreach (string task in tasks)
        {
            if (!int.TryParse(Path.GetFileName(task), out int tid) || tid == self) continue;
            if (sched_getaffinity(tid, MaskWords * sizeof(ulong), mask) != 0) continue;
            if ((mask[cpu / 64] & bit) == 0) continue;
            mask[cpu / 64] &= ~bit;
            if (!HasAny(mask) && s_processCpus is not null)
            {
                s_processCpus.CopyTo(new Span<ulong>(mask, MaskWords));
                mask[cpu / 64] &= ~bit;
            }

            // A thread with no other CPU keeps this one.
            if (HasAny(mask) && sched_setaffinity(tid, MaskWords * sizeof(ulong), mask) == 0) moved++;
        }
        return moved;
    }

    private static bool HasAny(ulong* mask)
    {
        for (int i = 0; i < MaskWords; i++)
        {
            if (mask[i] != 0) return true;
        }
        return false;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial long syscall(long number);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int sched_getaffinity(int pid, nuint size, ulong* mask);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int sched_setaffinity(int pid, nuint size, ulong* mask);
}
