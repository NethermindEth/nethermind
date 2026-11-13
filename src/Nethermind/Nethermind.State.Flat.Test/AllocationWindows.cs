// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Measures what a warm operation allocates on the current thread.
/// </summary>
/// <remarks>
/// Runs the operation in several identical windows. A one-off chunk the runtime charges to this thread (lazy thread-local
/// or static initialization) only adds bytes, and lands in one window only. Pick the method by the bound the test asserts.
/// </remarks>
public static class AllocationWindows
{
    public const int Count = 5;

    /// <summary>
    /// For an upper bound (the operation must not allocate, or must allocate less): drops the most expensive window and
    /// returns the most expensive of the rest, so an allocation the operation makes in more than one window still counts.
    /// </summary>
    public static long AllocatedBytes(Action operation)
    {
        Span<long> windows = stackalloc long[Count];
        Measure(operation, windows);
        return windows[^2];
    }

    /// <summary>
    /// For a lower bound (the operation must allocate): returns the cheapest window, so a one-off can never satisfy the
    /// bound, while an allocation the operation makes on every run is in every window.
    /// </summary>
    public static long MinAllocatedBytes(Action operation)
    {
        Span<long> windows = stackalloc long[Count];
        Measure(operation, windows);
        return windows[0];
    }

    private static void Measure(Action operation, Span<long> windows)
    {
        for (int i = 0; i < windows.Length; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            operation();
            windows[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        windows.Sort();
    }
}
