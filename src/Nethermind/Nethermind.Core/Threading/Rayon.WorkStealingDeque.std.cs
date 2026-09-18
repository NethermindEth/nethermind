// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.Threading;

public static partial class Rayon
{
    /// <summary>
    /// Chase-Lev work-stealing deque: a single owner pushes and pops at the bottom (LIFO), any number of
    /// thieves steal from the top (FIFO).
    /// </summary>
    /// <remarks>
    /// Lock-free per Chase &amp; Lev, "Dynamic Circular Work-Stealing Deque" (2005), with the memory
    /// ordering of Lê et al., "Correct and Efficient Work-Stealing for Weak Memory Models" (2013).
    /// Indices are 64-bit and never wrap. The buffer grows by copying; old buffers are never written
    /// again, so a thief that read a stale buffer reference still sees the element it indexed.
    /// Consumed slots are cleared by the owner (a popped slot at once, stolen slots when the deque is
    /// found empty), otherwise finished jobs and everything their state references would stay reachable
    /// until the ring wraps.
    /// </remarks>
    internal sealed class WorkStealingDeque
    {
        private const int InitialLogSize = 6;

        public enum StealResult
        {
            Success,
            Empty,
            Abort
        }

        private sealed class CircularArray(int logSize)
        {
            private readonly Job?[] _buffer = new Job?[1 << logSize];
            private readonly long _mask = (1 << logSize) - 1;

            public int Length => _buffer.Length;

            public Job? Get(long index) => _buffer[index & _mask];

            public void Put(long index, Job? job) => _buffer[index & _mask] = job;

            public void Clear() => Array.Clear(_buffer);

            public CircularArray Grow(long top, long bottom)
            {
                CircularArray grown = new(logSize + 1);
                for (long i = top; i < bottom; i++)
                {
                    grown.Put(i, Get(i));
                }

                return grown;
            }
        }

        private CacheLinePaddedLong _top;
        private CacheLinePaddedLong _bottom;
        private CircularArray _array = new(InitialLogSize);
        private bool _hasStaleSlots;

        public bool IsEmpty => Volatile.Read(ref _bottom.Value) <= Volatile.Read(ref _top.Value);

        /// <summary>Owner only.</summary>
        public void Push(Job job)
        {
            long bottom = _bottom.Value;
            long top = Volatile.Read(ref _top.Value);
            CircularArray array = _array;
            if (bottom - top >= array.Length)
            {
                array = array.Grow(top, bottom);
                Volatile.Write(ref _array, array);
            }

            array.Put(bottom, job);
            _hasStaleSlots = true;
            // Release store publishes the element to thieves.
            Volatile.Write(ref _bottom.Value, bottom + 1);
        }

        /// <summary>Owner only. Drops the references left in consumed slots once the deque is empty.</summary>
        public void ClearIfEmpty()
        {
            if (_hasStaleSlots && IsEmpty)
            {
                _hasStaleSlots = false;
                _array.Clear();
            }
        }

        /// <summary>Owner only. Returns the most recently pushed job, or null when empty or lost to a thief.</summary>
        public Job? Pop()
        {
            long bottom = _bottom.Value - 1;
            CircularArray array = _array;
            _bottom.Value = bottom;
            // Full fence: the store to bottom must be visible before top is loaded, otherwise the owner and a
            // thief can both take the last element. Volatile.Write alone is release-only and does not order
            // a store before a subsequent load.
            Interlocked.MemoryBarrier();
            long top = Volatile.Read(ref _top.Value);

            if (top > bottom)
            {
                _bottom.Value = bottom + 1;
                // Empty: every slot is consumed and no thief can still succeed on one (its CAS on top
                // would fail), so the stale references can go.
                if (_hasStaleSlots)
                {
                    _hasStaleSlots = false;
                    array.Clear();
                }

                return null;
            }

            Job? job = array.Get(bottom);
            if (top != bottom)
            {
                array.Put(bottom, null);
                return job;
            }

            // Last element: race the thieves for it.
            if (Interlocked.CompareExchange(ref _top.Value, top + 1, top) != top)
            {
                job = null;
            }
            else
            {
                array.Put(bottom, null);
            }

            _bottom.Value = bottom + 1;
            return job;
        }

        /// <summary>Any thread. Takes the oldest job.</summary>
        public StealResult TrySteal(out Job? job)
        {
            long top = Volatile.Read(ref _top.Value);
            // Pairs with the fence in Pop so that a thief observing the owner's decremented bottom also
            // observes the corresponding top.
            Interlocked.MemoryBarrier();
            long bottom = Volatile.Read(ref _bottom.Value);
            job = null;
            if (top >= bottom)
            {
                return StealResult.Empty;
            }

            // Read the array after top/bottom: whichever buffer is observed, [top, bottom) is intact in it.
            CircularArray array = Volatile.Read(ref _array);
            Job? candidate = array.Get(top);
            if (Interlocked.CompareExchange(ref _top.Value, top + 1, top) != top)
            {
                return StealResult.Abort;
            }

            job = candidate;
            return StealResult.Success;
        }
    }
}
