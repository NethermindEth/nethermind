// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Nethermind.Core.Utils;

/// <summary>
/// The lock-free lease-counter algorithm shared by <see cref="RefCountingDisposable"/> and
/// <see cref="SmallRefCountingDisposable"/>. The two types differ only in how they store the counter
/// (cache-line-padded vs. inline); the compare-and-swap logic operating on it is identical and lives
/// here so it is not duplicated between them.
/// </summary>
public static class RefCountingLease
{
    public const int Single = 1;
    public const int NoAccessors = 0;
    public const int Disposing = -1;

    /// <summary>
    /// Attempts to increment the lease count, returning <c>false</c> once the object is being torn down.
    /// </summary>
    public static bool TryAcquire(ref long leases)
    {
        // Volatile read for starting value
        long current = Volatile.Read(ref leases);

        while (true)
        {
            // Reject once the count has reached zero (NoAccessors) or gone to Disposing: the object is
            // being torn down. Acquiring at NoAccessors would resurrect an object whose owner has
            // already observed the zero count and begun teardown — the release path moves the count
            // 1 → 0 and only then CASes 0 → Disposing, so a concurrent acquirer can briefly see 0.
            // Checking inside the loop (not just on the initial read) also closes the window where a
            // failed CAS hands back a now-zero count.
            if (current <= NoAccessors)
            {
                return false;
            }

            long prev = Interlocked.CompareExchange(ref leases, current + Single, current);
            if (prev == current)
            {
                // Successfully acquired
                return true;
            }

            // Try again with the observed value
            current = prev;
            // Add PAUSE instruction to reduce shared core contention
            Thread.SpinWait(1);
        }
    }

    /// <summary>
    /// Decrements the lease count by one, returning <c>true</c> when the caller performed the exclusive
    /// last release and must therefore run cleanup exactly once.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The lease has already been fully released.</exception>
    public static bool ReleaseOnce(ref long leases)
    {
        // Volatile read for starting value
        long current = Volatile.Read(ref leases);

        while (true)
        {
            // Re-validate on every iteration (the initial read and each failed-CAS retry). A failed CAS
            // hands back the observed value, which a concurrent release can have already driven to
            // NoAccessors — or to Disposing — inside the teardown window. Decrementing from there would
            // write Disposing(-1) via the subtract below, spuriously and bypassing the dedicated
            // NoAccessors -> Disposing CAS, leaving the genuine last-releaser's CleanUp unrun.
            if (current <= NoAccessors)
            {
                // Mismatched Acquire/Release
                ThrowOverDisposed();
            }

            long prev = Interlocked.CompareExchange(ref leases, current - Single, current);
            if (prev != current)
            {
                current = prev;
                // Add PAUSE instruction to reduce shared core contention
                Thread.SpinWait(1);
                continue;
            }
            if (prev == Single)
            {
                // Last use, try to dispose underlying
                break;
            }

            // Successfully released (prev > Single here, guaranteed > NoAccessors by the check above)
            return false;
        }

        // Only the thread that won the exclusive 1 -> 0 decrement above reaches here, and nothing can move
        // the count off 0 afterwards (a concurrent acquire refuses at 0, a concurrent release throws
        // over-disposed), so this CAS always succeeds and the caller runs cleanup exactly once.
        return Interlocked.CompareExchange(ref leases, Disposing, NoAccessors) == NoAccessors;

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowOverDisposed() => throw new ObjectDisposedException("The lease has already been disposed");
    }

    /// <summary>
    /// Renders the counter for diagnostics: <c>"Disposed"</c> once torn down, otherwise the live count.
    /// </summary>
    public static string Describe(long leases) => leases == Disposing ? "Disposed" : $"Leases: {leases}";
}
