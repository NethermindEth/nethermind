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

    [LibraryImport("libc", SetLastError = true)]
    private static partial int sched_getaffinity(int pid, nuint size, ulong* mask);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int sched_setaffinity(int pid, nuint size, ulong* mask);
}
