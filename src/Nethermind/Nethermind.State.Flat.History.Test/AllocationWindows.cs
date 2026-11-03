// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State.Flat.History.Test;

/// <summary>
/// Measures what a warm operation allocates on the current thread.
/// </summary>
/// <remarks>
/// Runs the operation in several identical windows, drops the most expensive window and returns the most expensive of
/// the rest. A one-off chunk the runtime charges to this thread lands in one window only, while an allocation the
/// operation makes on every run, or in more than one window, still counts.
/// </remarks>
internal static class AllocationWindows
{
    public const int Count = 5;

    public static long AllocatedBytes(Action operation)
    {
        Span<long> windows = stackalloc long[Count];
        for (int i = 0; i < Count; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            operation();
            windows[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        windows.Sort();
        return windows[^2];
    }
}
