using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.Core.Utils;

/// <summary>
/// Provides a component that can be disposed multiple times and runs <see cref="CleanUp"/> only on the last dispose.
/// </summary>
public abstract class RefCountingDisposable : IDisposable
{
    private const int Single = 1;
    private const int NoAccessors = 0;
    private const int Disposing = -1;

    protected PaddedValue _leases;

    protected RefCountingDisposable(int initialCount = Single)
    {
        _leases.Value = initialCount;
    }

    public void AcquireLease()
    {
        if (!TryAcquireLease())
        {
            ThrowCouldNotAcquire();
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowCouldNotAcquire()
        {
            throw new InvalidOperationException("The lease cannot be acquired");
        }
    }

    protected bool TryAcquireLease()
    {
        // Volatile read for starting value
        long current = Volatile.Read(ref _leases.Value);
        if (current == Disposing)
        {
            // Already disposed
            return false;
        }

        while (true)
        {
            long prev = Interlocked.CompareExchange(ref _leases.Value, current + Single, current);
            if (prev == current)
            {
                // Successfully acquired
                return true;
            }
            if (prev == Disposing)
            {
                // Already disposed
                return false;
            }

            // Try again with new starting value
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
        long current = Volatile.Read(ref _leases.Value);
        if (current <= NoAccessors)
        {
            // Mismatched Acquire/Release
            ThrowOverDisposed();
        }

        while (true)
        {
            long prev = Interlocked.CompareExchange(ref _leases.Value, current - Single, current);
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
            if (prev <= NoAccessors)
            {
                // Mismatched Acquire/Release
                ThrowOverDisposed();
            }

            // Successfully released
            return;
        }

        if (Interlocked.CompareExchange(ref _leases.Value, Disposing, NoAccessors) == NoAccessors)
        {
            // set to disposed by this Release
            CleanUp();
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowOverDisposed()
        {
            throw new ObjectDisposedException("The lease has already been disposed");
        }
    }

    protected abstract void CleanUp();

    public override string ToString()
    {
        var leases = Volatile.Read(ref _leases.Value);
        return leases == Disposing ? "Disposed" : $"Leases: {leases}";
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    protected struct PaddedValue
    {
        [FieldOffset(64)]
        public long Value;
    }
}
