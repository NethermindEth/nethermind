// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Nethermind.Core.Utils;

/// <summary>
/// Variant of <see cref="RefCountingDisposable"/> that stores its lease counter inline as a single
/// <see cref="long"/> instead of a cache-line-padded one, trading false-sharing protection for a much
/// smaller per-instance footprint. Prefer it for types that exist in large numbers and whose lease
/// counts are rarely contended across cores.
/// </summary>
public abstract class SmallRefCountingDisposable(int initialCount = 1) : IDisposable
{
    private const int Single = 1;
    private const int NoAccessors = 0;
    private const int Disposing = -1;

    private long _leases = initialCount;

    public void AcquireLease()
    {
        if (!TryAcquireLease())
        {
            ThrowCouldNotAcquire();
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowCouldNotAcquire() => throw new InvalidOperationException("The lease cannot be acquired");
    }

    protected bool TryAcquireLease()
    {
        // Volatile read for starting value
        long current = Volatile.Read(ref _leases);

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

            long prev = Interlocked.CompareExchange(ref _leases, current + Single, current);
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
    /// Disposes it once, decreasing the lease count by 1.
    /// </summary>
    public void Dispose() => ReleaseLeaseOnce();

    private void ReleaseLeaseOnce()
    {
        // Volatile read for starting value
        long current = Volatile.Read(ref _leases);

        while (true)
        {
            // Re-validate on every iteration (the initial read and each failed-CAS retry). A failed CAS
            // hands back the observed value, which a concurrent release can have already driven to
            // NoAccessors — or to Disposing — inside the teardown window. Decrementing from there would
            // write Disposing(-1) via the subtract below, spuriously and bypassing the dedicated
            // NoAccessors -> Disposing CAS, leaving the genuine last-releaser's CleanUp unrun. Mirrors
            // the guard in TryAcquireLease.
            if (current <= NoAccessors)
            {
                // Mismatched Acquire/Release
                ThrowOverDisposed();
            }

            long prev = Interlocked.CompareExchange(ref _leases, current - Single, current);
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
            return;
        }

        // Only the thread that won the exclusive 1 -> 0 decrement above reaches here, and nothing can move
        // _leases off 0 afterwards (a concurrent acquire refuses at 0, a concurrent release throws
        // over-disposed), so this CAS always succeeds and CleanUp runs exactly once — no double dispose.
        if (Interlocked.CompareExchange(ref _leases, Disposing, NoAccessors) == NoAccessors)
        {
            CleanUp();
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowOverDisposed() => throw new ObjectDisposedException("The lease has already been disposed");
    }

    protected abstract void CleanUp();

    public override string ToString()
    {
        long leases = Volatile.Read(ref _leases);
        return leases == Disposing ? "Disposed" : $"Leases: {leases}";
    }
}
