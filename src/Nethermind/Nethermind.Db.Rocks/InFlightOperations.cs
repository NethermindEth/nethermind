// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;

namespace Nethermind.Db.Rocks;

/// <summary>
/// Counts operations that must finish before their owner releases what they use. After <see cref="Close"/>, no new
/// operation starts, and <see cref="WaitForAll"/> waits for the ones that started before it.
/// </summary>
/// <remarks>
/// An operation counts itself before it reads the closed flag, and <see cref="Close"/> sets the flag before the count
/// is read; both are full fences, so each operation is either refused or waited for.
/// </remarks>
internal sealed class InFlightOperations
{
    private int _count;
    private int _closed;

    /// <summary>The operations that have started and not ended.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Starts an operation unless the owner is closed. A started operation calls <see cref="End"/> once.</summary>
    public bool TryBegin()
    {
        Interlocked.Increment(ref _count);
        if (Volatile.Read(ref _closed) == 0) return true;
        Interlocked.Decrement(ref _count);
        return false;
    }

    public void End() => Interlocked.Decrement(ref _count);

    /// <summary>Refuses every operation that has not started yet.</summary>
    public void Close() => Interlocked.Exchange(ref _closed, 1);

    /// <summary>Waits up to <paramref name="timeout"/> for the started operations to end.</summary>
    /// <returns>Whether all of them ended.</returns>
    public bool WaitForAll(TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();
        SpinWait spin = default;
        while (Volatile.Read(ref _count) != 0)
        {
            if (Stopwatch.GetElapsedTime(start) > timeout) return false;
            spin.SpinOnce();
        }

        return true;
    }
}
