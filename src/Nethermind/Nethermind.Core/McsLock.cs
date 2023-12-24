// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core;

/// <summary>
/// MCSLock (Mellor-Crummey and Scott Lock) provides a fair, scalable mutual exclusion lock.
/// This lock is particularly effective in systems with a high number of threads, as it reduces
/// the contention and spinning overhead typical of other spinlocks. It achieves this by forming
/// a queue of waiting threads, ensuring each thread gets the lock in the order it was requested.
/// </summary>
public class McsLock
{
    private readonly int HalfCores = Math.Max(Environment.ProcessorCount / 2, 1);

    /// <summary>
    /// Thread-local storage to ensure each thread has its own node instance.
    /// </summary>
    private readonly ThreadLocal<ThreadNode> _node = new(() => new ThreadNode());

    /// <summary>
    /// Used to throttle Normal priority threads
    /// </summary>
    private readonly SemaphoreSlim _semaphore;

    /// <summary>
    /// Points to the last node in the queue (tail). Used to manage the queue of waiting threads.
    /// </summary>
    private volatile ThreadNode? _tail;

    public McsLock()
    {
        _semaphore = new SemaphoreSlim(initialCount: HalfCores, maxCount: HalfCores);
    }

    /// <summary>
    /// Acquires the lock. If the lock is already held, the calling thread is placed into a queue and
    /// enters a busy-wait state until the lock becomes available.
    /// </summary>
    public Disposable Acquire()
    {
        bool isPriority = Thread.CurrentThread.Priority > ThreadPriority.Normal;
        if (!isPriority)
        {
            // If not a priority thread max of half processors can being to acquire the lock (e.g. block processing)
            _semaphore.Wait();
        }


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
            while (node.Locked)
            {
                sw.SpinOnce();
            }

        }

        return new Disposable(this, releaseSemaphore: !isPriority);
    }

    /// <summary>
    /// Used to releases the lock. If there are waiting threads in the queue, it passes the lock to the next
    /// thread in line.
    /// </summary>
    public readonly struct Disposable : IDisposable
    {
        readonly McsLock _lock;
        readonly bool _releaseSemaphore;

        internal Disposable(McsLock @lock, bool releaseSemaphore)
        {
            _lock = @lock;
            _releaseSemaphore = releaseSemaphore;
        }

        /// <summary>
        /// Releases the lock. If there are waiting threads in the queue, it passes the lock to the next
        /// thread in line.
        /// </summary>
        public void Dispose()
        {
            if (_releaseSemaphore)
            {
                _lock._semaphore.Release();
            }

            ThreadNode node = _lock._node.Value!;

            // If there is no next node, it means this thread might be the last in the queue.
            if (node.Next == null)
            {
                // Attempt to atomically set the tail to null, indicating no thread is waiting.
                // If it is still 'node', then there are no other waiting threads.
                if (Interlocked.CompareExchange(ref _lock._tail, null, node) == node)
                {
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

            // Pass the lock to the next thread by setting its 'Locked' flag to false.
            node.Next.Locked = false;
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
