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
    private readonly ThreadLocal<ThreadNode> _node = new(() => new ThreadNode());

    /// <summary>
    /// Points to the last node in the queue (tail). Used to manage the queue of waiting threads.
    /// </summary>
    private volatile ThreadNode? _tail;

    internal volatile Thread? currentLockHolder = null;

    /// <summary>
    /// Acquires the lock. If the lock is already held, the calling thread is placed into a queue and
    /// enters a busy-wait state until the lock becomes available.
    /// </summary>
    public Disposable Acquire()
    {
        // Check for reentrancy.
        if (Thread.CurrentThread == currentLockHolder)
            ThrowInvalidOperationException();

        ThreadNode node = _node.Value!;
        node.Locked = true;

        ThreadNode? predecessor = Interlocked.Exchange(ref _tail, node);
        if (predecessor is not null)
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
            while (node.Locked)
            {
                if (sw.NextSpinWillYield)
                {
                    // We use Monitor signalling to try to combat additional latency
                    // that may be introduced by the strict in-order thread queuing
                    // rather than letting the SpinWait sleep the thread.
                    lock (node)
                    {
                        if (node.Locked)
                            // Sleep till signal
                            Monitor.Wait(node);
                        else
                        {
                            // Acquired the lock
                            break;
                        }
                    }
                }
                else
                {
                    sw.SpinOnce();
                }
            }
        }

        // Set current lock holder.
        currentLockHolder = Thread.CurrentThread;

        return new Disposable(this);

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException("Lock is not reentrant");
        }
    }

    /// <summary>
    /// Used to releases the lock. If there are waiting threads in the queue, it passes the lock to the next
    /// thread in line.
    /// </summary>
    public readonly struct Disposable : IDisposable
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
            if (node.Next == null)
            {
                // Attempt to atomically set the tail to null, indicating no thread is waiting.
                // If it is still 'node', then there are no other waiting threads.
                if (Interlocked.CompareExchange(ref _lock._tail, null, node) == node)
                {
                    // Clear current lock holder.
                    _lock.currentLockHolder = null;
                    return;
                }

                // If another thread is in the process of enqueuing itself,
                // wait until it finishes setting its node as the 'Next' node.
                SpinWait sw = default;
                while (node.Next == null)
                {
                    sw.SpinOnce();
                }
            }

            // Clear current lock holder.
            _lock.currentLockHolder = null;

            // Pass the lock to the next thread by setting its 'Locked' flag to false.
            node.Next.Locked = false;

            lock (node.Next)
            {
                // Wake up next node if sleeping
                Monitor.Pulse(node.Next);
            }
            // Remove the reference to the next node 
            node.Next = null;
        }
    }

    /// <summary>
    /// Node class to represent each thread in the MCS lock queue.
    /// </summary>
    private class ThreadNode
    {
        /// <summary>
        /// Indicates whether the current thread is waiting for the lock.
        /// </summary>
        public volatile bool Locked = true;

        /// <summary>
        /// Points to the next node in the queue.
        /// </summary>
        public ThreadNode? Next = null;
    }
}
