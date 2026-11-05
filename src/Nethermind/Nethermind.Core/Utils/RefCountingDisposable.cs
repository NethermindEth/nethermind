// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core.Threading;

namespace Nethermind.Core.Utils;

/// <summary>
/// Provides a component that can be disposed multiple times and runs <see cref="CleanUp"/> only on the last dispose.
/// </summary>
/// <remarks>
/// The lease counter lives in a <see cref="CacheLinePaddedLong"/> so concurrent atomic updates do not
/// suffer false sharing with neighbouring fields. See <see cref="SmallRefCountingDisposable"/> for a
/// variant that stores the counter inline for types that exist in large numbers. The lease algorithm
/// itself is shared through <see cref="RefCountingLease"/>.
/// </remarks>
public abstract class RefCountingDisposable : IDisposable
{
    protected CacheLinePaddedLong _leases;

    protected RefCountingDisposable(int initialCount = RefCountingLease.Single) => _leases.Value = initialCount;

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

    protected bool TryAcquireLease() => RefCountingLease.TryAcquire(ref _leases.Value);

    /// <summary>
    /// Disposes it once, decreasing the lease count by 1.
    /// </summary>
    public void Dispose()
    {
        if (RefCountingLease.ReleaseOnce(ref _leases.Value))
        {
            CleanUp();
        }
    }

    protected abstract void CleanUp();

    public override string ToString() => RefCountingLease.Describe(Volatile.Read(ref _leases.Value));
}
