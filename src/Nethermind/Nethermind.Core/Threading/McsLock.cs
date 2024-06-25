// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Nethermind.Core.Threading;

/// <summary>
/// MCSLock (Mellor-Crummey and Scott Lock) provides a fair, scalable mutual exclusion lock.
/// This lock is particularly effective in systems with a high number of threads, as it reduces
/// the contention and spinning overhead typical of other spinlocks. It achieves this by forming
/// a queue of waiting threads, ensuring each thread gets the lock in the order it was requested.
/// </summary>
public class McsLock
{
    /// <summary>
    /// Thread-local storage to ensure each thread has its own node instance.
    /// </summary>
    internal readonly ThreadLocal<ThreadNode> _node = new(() => new ThreadNode());

    /// <summary>
    /// Points to the last node in the queue (tail). Used to manage the queue of waiting threads.
    /// </summary>
    private volatile ThreadNode? _tail;

    internal volatile ThreadNode? _currentLockHolder = null;

    /// <summary>
    /// Acquires the lock. If the lock is already held, the calling thread is placed into a queue and
    /// enters a busy-wait state until the lock becomes available.
    /// </summary>
    public Disposable Acquire()
    {
        ThreadNode node = _node.Value!;

        // Check for reentrancy.
        if (ReferenceEquals(node, _currentLockHolder))
            ThrowInvalidOperationException();

        node.State = (nuint)LockState.Waiting;

        ThreadNode? predecessor = Interlocked.Exchange(ref _tail, node);
        if (predecessor is not null)
        {
            WaitForUnlock(node, predecessor);
        }

        // Set current lock holder.
        _currentLockHolder = node;

        return new Disposable(this);

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException("Lock is not reentrant");
        }
    }

    private static void WaitForUnlock(ThreadNode node, ThreadNode predecessor)
    {
        // If there was a previous tail, it means the lock is already held by someone.
        // Set this node as the next node of the predecessor.
        predecessor.Next = node;

        // Busy-wait (spin) until our 'Locked' flag is set to false by the thread
        // that is releasing the lock.
        SpinWait sw = default;
        // This lock is more scalable than regular locks as each thread
        // spins on their own local flag rather than a shared flag for
        // lower cpu cache thrashing. Drawback is it is a strict queue and
        // the next thread in line may be sleeping when lock is released.
        while (node.State != (nuint)LockState.ReadyToAcquire)
        {
            if (sw.NextSpinWillYield)
            {
                WaitForSignal(node);
                // Acquired the lock
                break;
            }
            else
            {
                sw.SpinOnce();
            }
        }

        node.State = (nuint)LockState.Acquired;
        Interlocked.MemoryBarrier();
    }

    private static void WaitForSignal(ThreadNode node)
    {
        // We use Monitor signalling to try to combat additional latency
        // that may be introduced by the strict in-order thread queuing
        // rather than letting the SpinWait sleep the thread.
        lock (node)
        {
            if (node.State != (nuint)LockState.ReadyToAcquire)
            {
                // Sleep till signal
                Monitor.Wait(node);
            }
        }
    }

    /// <summary>
    /// Used to releases the lock. If there are waiting threads in the queue, it passes the lock to the next
    /// thread in line.
    /// </summary>
    public readonly ref struct Disposable
    {
        readonly McsLock _lock;

        internal Disposable(McsLock @lock)
        {
            _lock = @lock;
        }

        /// <summary>
        /// Releases the lock. If there are waiting threads in the queue, it passes the lock to the next
        /// thread in line.
        /// </summary>
        public void Dispose()
        {
            ThreadNode node = _lock._node.Value!;

            // If there is no next node, it means this thread might be the last in the queue.
            if (node.Next is null)
            {
                // Attempt to atomically set the tail to null, indicating no thread is waiting.
                // If it is still 'node', then there are no other waiting threads.
                if (Interlocked.CompareExchange(ref _lock._tail, null, node) == node)
                {
                    // Clear current lock holder.
                    _lock._currentLockHolder = null;
                    return;
                }

                if (node.Next is null)
                {
                    SpinTillNextNotNull(node);
                }
            }

            ThreadNode next = node.Next!;
            // Clear current lock holder.
            _lock._currentLockHolder = null;
            // Pass the lock to the next thread by setting its 'Locked' flag to false.
            next.State = (nuint)LockState.ReadyToAcquire;

            // Give the next thread a chance to acquire the lock before checking.
            Interlocked.MemoryBarrier();

            if (next.State == (nuint)LockState.ReadyToAcquire)
            {
                SignalUnlock(next);
            }
            // Remove the reference to the next node 
            node.Next = null;
        }

        private static void SpinTillNextNotNull(ThreadNode node)
        {
            // If another thread is in the process of enqueuing itself,
            // wait until it finishes setting its node as the 'Next' node.
            SpinWait sw = default;
            while (node.Next is null)
            {
                sw.SpinOnce();
            }
        }

        private static void SignalUnlock(ThreadNode nextNode)
        {
            lock (nextNode)
            {
                if (nextNode.State == (nuint)LockState.ReadyToAcquire)
                {
                    // Wake up next node if sleeping
                    Monitor.Pulse(nextNode);
                }
            }
        }
    }

    private enum LockState : uint
    {
        ReadyToAcquire = 0,
        Waiting = 1,
        Acquired = 2
    }

    /// <summary>
    /// Node class to represent each thread in the MCS lock queue.
    /// </summary>
    internal class ThreadNode
    {
        /// <summary>
        /// Indicates whether the current thread is waiting for the lock.
        /// </summary>
        public volatile nuint State = (nuint)LockState.Waiting;

        /// <summary>
        /// Points to the next node in the queue.
        /// </summary>
        public ThreadNode? Next = null;
    }
}
