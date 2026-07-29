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
/// <remarks>
/// Only the counter storage differs from <see cref="RefCountingDisposable"/>; the lease algorithm is
/// shared through <see cref="RefCountingLease"/>.
/// </remarks>
public abstract class SmallRefCountingDisposable(int initialCount = RefCountingLease.Single) : IDisposable
{
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

    protected bool TryAcquireLease() => RefCountingLease.TryAcquire(ref _leases);

    /// <summary>
    /// Disposes it once, decreasing the lease count by 1.
    /// </summary>
    public void Dispose()
    {
        if (RefCountingLease.ReleaseOnce(ref _leases))
        {
            CleanUp();
        }
    }

    protected abstract void CleanUp();

    public override string ToString() => RefCountingLease.Describe(Volatile.Read(ref _leases));
}
