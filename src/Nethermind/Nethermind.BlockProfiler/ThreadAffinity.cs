// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.BlockProfiler;

/// <summary>Pins the calling thread to one CPU and restores its previous set, through Linux sched_setaffinity.</summary>
internal static unsafe partial class ThreadAffinity
{
    // cpu_set_t is 1024 bits.
    private const int MaskWords = 16;

    /// <summary>Moves the calling thread onto <paramref name="cpu"/>; returns the mask to restore, or <c>null</c> when refused.</summary>
    public static ulong[]? PinCurrentThread(int cpu)
    {
        if (!OperatingSystem.IsLinux() || cpu < 0 || cpu >= MaskWords * 64) return null;

        ulong[] previous = new ulong[MaskWords];
        fixed (ulong* p = previous)
        {
            if (sched_getaffinity(0, MaskWords * sizeof(ulong), p) != 0) return null;
        }

        ulong* mask = stackalloc ulong[MaskWords];
        new Span<ulong>(mask, MaskWords).Clear();
        mask[cpu / 64] = 1UL << (cpu % 64);
        // pid 0 is the calling thread; the kernel moves it onto the CPU before returning.
        return sched_setaffinity(0, MaskWords * sizeof(ulong), mask) == 0 ? previous : null;
    }

    public static void Restore(ulong[] previous)
    {
        fixed (ulong* p = previous) sched_setaffinity(0, MaskWords * sizeof(ulong), p);
    }

    /// <summary>
    /// Takes <paramref name="cpu"/> out of the CPU set of every other thread of the process that may still run on it,
    /// so the pinned thread has the core to itself; threads created later inherit their creator's set. Returns how
    /// many threads were moved.
    /// </summary>
    public static int ExcludeFromOtherThreads(int cpu)
    {
        if (!OperatingSystem.IsLinux() || cpu < 0 || cpu >= MaskWords * 64) return 0;

        int self = CurrentOsThreadId();
        int moved = 0;
        ulong* mask = stackalloc ulong[MaskWords];
        string[] tasks;
        try
        {
            tasks = System.IO.Directory.GetDirectories("/proc/self/task");
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        foreach (string task in tasks)
        {
            if (!int.TryParse(System.IO.Path.GetFileName(task), out int tid) || tid == self) continue;
            if (sched_getaffinity(tid, MaskWords * sizeof(ulong), mask) != 0) continue;
            ulong bit = 1UL << (cpu % 64);
            if ((mask[cpu / 64] & bit) == 0) continue;
            mask[cpu / 64] &= ~bit;
            bool othersLeft = false;
            for (int i = 0; i < MaskWords && !othersLeft; i++) othersLeft = mask[i] != 0;
            // A thread with no other CPU keeps this one.
            if (othersLeft && sched_setaffinity(tid, MaskWords * sizeof(ulong), mask) == 0) moved++;
        }
        return moved;
    }

    /// <summary>The calling thread's kernel id (gettid), or -1 off Linux.</summary>
    public static int CurrentOsThreadId() =>
        OperatingSystem.IsLinux() ? (int)syscall(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 178 : 186) : -1;

    [LibraryImport("libc", SetLastError = true)]
    private static partial long syscall(long number);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int sched_getaffinity(int pid, nuint size, ulong* mask);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int sched_setaffinity(int pid, nuint size, ulong* mask);
}
