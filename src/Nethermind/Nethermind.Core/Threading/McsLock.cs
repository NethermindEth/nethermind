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
/// <remarks>
/// The waiter at the head of the queue uses a bounded yield phase before parking, while deeper
/// waiters park promptly. The parked state is published atomically so the releasing thread only
/// pays the lock-and-pulse when its successor is actually asleep. Reentrant acquisition throws
/// instead of silently self-deadlocking on the thread's own queue node.
/// </remarks>
public class McsLock
{
    /// <summary>
    /// Yield-phase iterations the queue-head waiter spends before parking anyway. Bounds the
    /// spin when the lock holder is descheduled or the critical section is unexpectedly long;
    /// short critical sections hand off within the initial busy-spin phase.
    /// </summary>
    private const int HeadSpinYieldLimit = 64;

    /// <summary>
    /// Thread-local storage to ensure each thread has its own node instance.
    /// </summary>
    private readonly ThreadLocal<ThreadNode> _node;

    /// <summary>
    /// Points to the last node in the queue (tail).
    /// </summary>
    private ThreadNode? _tail;

    public McsLock() => _node = new(() => new ThreadNode(this));

    internal ThreadNode CurrentThreadNodeForTesting => _node.Value!;

    /// <summary>
    /// Acquires the lock. If the lock is already held, the calling thread is placed into a queue and
    /// spins or sleeps until the lock becomes available.
    /// </summary>
    /// <exception cref="InvalidOperationException">The current thread already holds this lock.</exception>
    public Disposable EnterScope()
    {
        ThreadNode node = _node.Value!;

        // A node that is not Unlocked means this thread is already waiting on or holding this
        // lock; enqueueing it again would self-deadlock, so fail loud instead.
        if (node.State != (nuint)LockState.Unlocked)
            ThrowInvalidOperationException();

        node.State = (nuint)LockState.Waiting;

        ThreadNode? predecessor = Interlocked.Exchange(ref _tail, node);
        if (predecessor is not null)
        {
            WaitForUnlock(node, predecessor);
        }

        // Owner state doubles as the successor's head-of-queue signal: a waiter whose
        // predecessor is no longer Waiting knows it is next in line and keeps spinning.
        node.State = (nuint)LockState.Acquired;

        return new Disposable(node);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidOperationException()
            => throw new InvalidOperationException("Lock is not reentrant and is already held by this thread.");
    }

    /// <summary>
    /// Acquires the lock using the legacy method name.
    /// </summary>
    public Disposable Acquire() => EnterScope();

    private static void WaitForUnlock(ThreadNode node, ThreadNode predecessor)
    {
        // If there was a previous tail, it means the lock is already held by someone.
        // Set this node as the next node of the predecessor.
        predecessor.Next = node;

        // This lock is more scalable than regular locks as each thread
        // spins on their own local flag rather than a shared flag for
        // lower cpu cache thrashing. Drawback is it is a strict queue and
        // a sleeping waiter adds wake latency at handoff - so the head of
        // the queue gets a bounded yield phase before it parks.
        SpinWait sw = default;
        int headYields = 0;
        while (node.State != (nuint)LockState.ReadyToAcquire)
        {
            if (sw.NextSpinWillYield)
            {
                // Head of the queue iff the predecessor already took the lock (or released it);
                // a predecessor still Waiting means threads are queued ahead of us.
                nuint predecessorState = predecessor.State;
                bool isQueueHead = predecessorState != (nuint)LockState.Waiting &&
                    predecessorState != (nuint)LockState.Parked;
                if (!isQueueHead || ++headYields > HeadSpinYieldLimit)
                {
                    WaitForSignal(node);
                    if (node.State == (nuint)LockState.ReadyToAcquire)
                    {
                        // Acquired the lock
                        break;
                    }

                    // Defensively restart the spin phase if a wake occurs without handoff.
                    sw.Reset();
                    headYields = 0;
                }
                else
                {
                    // Yield the core but never Sleep(1): a millisecond-class sleep at the queue
                    // head would add more handoff latency than parking on the Monitor does.
                    sw.SpinOnce(sleep1Threshold: -1);
                }
            }
            else
            {
                sw.SpinOnce(sleep1Threshold: -1);
            }
        }
    }

    private static void WaitForSignal(ThreadNode node)
    {
        // We use Monitor signalling to try to combat additional latency
        // that may be introduced by the strict in-order thread queuing
        // rather than letting the SpinWait sleep the thread.
        lock (node)
        {
            // The exchange either publishes Parked before the releaser publishes ReadyToAcquire,
            // or observes that handoff and avoids sleeping. Holding the node monitor closes the
            // interval between publishing Parked and entering Monitor.Wait.
            if (Interlocked.CompareExchange(
                ref node.State,
                (nuint)LockState.Parked,
                (nuint)LockState.Waiting) == (nuint)LockState.Waiting)
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
        readonly ThreadNode _node;

        internal Disposable(ThreadNode node) => _node = node;

        /// <summary>
        /// Releases the lock. If there are waiting threads in the queue, it passes the lock to the next
        /// thread in line.
        /// </summary>
        public void Dispose()
        {
            ThreadNode node = _node;
            // If there is no next node, it means this thread might be the last in the queue.
            if (node.Next is null)
            {
                // Attempt to atomically set the tail to null, indicating no thread is waiting.
                // If it is still 'node', then there are no other waiting threads.
                if (Interlocked.CompareExchange(ref node.Lock._tail, null, node) == node)
                {
                    node.State = (nuint)LockState.Unlocked;
                    return;
                }

                if (node.Next is null)
                {
                    SpinTillNextNotNull(node);
                }
            }

            ThreadNode next = node.Next!;
            // Re-arm reentrancy detection for this thread before handing off.
            node.State = (nuint)LockState.Unlocked;
            // Publish the handoff and atomically learn whether the successor committed to sleep.
            // A waiter that loses its Waiting -> Parked compare-exchange observes this state and
            // cannot enter Monitor.Wait, so no separate fence or parked flag is required.
            nuint previousState = Interlocked.Exchange(ref next.State, (nuint)LockState.ReadyToAcquire);
            if (previousState == (nuint)LockState.Parked)
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

    internal enum LockState : uint
    {
        ReadyToAcquire = 0,
        Waiting = 1,
        Acquired = 2,
        Unlocked = 3,
        Parked = 4
    }

    /// <summary>
    /// Node class to represent each thread in the MCS lock queue.
    /// </summary>
    internal sealed class ThreadNode(McsLock @lock)
    {
        /// <summary>
        /// Whether the owning thread is waiting for, holding, or done with the lock. Spun on by
        /// the owning thread; written by the predecessor at handoff.
        /// </summary>
        public volatile nuint State = (nuint)LockState.Unlocked;

        /// <summary>
        /// Points to the next node in the queue.
        /// </summary>
        public volatile ThreadNode? Next = null;

        /// <summary>
        /// The lock for access to _tail
        /// </summary>
        public readonly McsLock Lock = @lock;
    }
}
