// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Threading;

namespace Nethermind.Core.Threading;

/// <summary>
/// MCSLock (Mellor-Crummey and Scott Lock) provides a fair, scalable mutual exclusion lock.
/// The McsPriorityLock allows higher priority threads to queue jump on the lock queue.
/// This lock is particularly effective in systems with a high number of threads, as it reduces
/// the contention and spinning overhead typical of other spinlocks. It achieves this by forming
/// a queue of waiting threads, ensuring each thread gets the lock in the order it was requested.
/// </summary>
public class McsPriorityLock
{
    private readonly int HalfCores = Math.Max(Environment.ProcessorCount / 2, 1);

    private readonly McsLock _coreLock = new();
    private readonly McsLock[] _queuedLocks;
    private uint _queueId;

    public McsPriorityLock()
    {
        var queue = new McsLock[HalfCores];
        for (var i = 0; i < queue.Length; i++)
        {
            queue[i] = new McsLock();
        }

        _queuedLocks = queue;
    }

    /// <summary>
    /// Acquires the lock. If the lock is already held, the calling thread is placed into a queue and
    /// enters a busy-wait state until the lock becomes available.
    ///
    /// Higher priority threads will queue jump.
    /// </summary>
    public McsLock.Disposable Acquire()
    {
        // Check for reentrancy.
        if (Thread.CurrentThread == _coreLock.currentLockHolder)
            ThrowInvalidOperationException();

        var isPriority = Thread.CurrentThread.Priority > ThreadPriority.Normal;
        if (!isPriority)
            // If not a priority thread max of half processors can being to acquire the lock (e.g. block processing)
            return NonPriorityAcquire();

        return _coreLock.Acquire();

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException("Lock is not reentrant");
        }
    }

    private McsLock.Disposable NonPriorityAcquire()
    {
        var queueId = Interlocked.Increment(ref _queueId) % (uint)_queuedLocks.Length;

        using var handle = _queuedLocks[queueId].Acquire();

        return _coreLock.Acquire();
    }
}
