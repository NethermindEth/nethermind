// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.State.Flat
{
    /// <summary>
    /// Lock-free MPMC bounded stack for struct T.
    ///
    /// Uses:
    /// - Preallocated node pool
    /// - Two Treiber stacks (free / used)
    /// - ABA protection via tagged heads
    /// </summary>
    public sealed class MpmcBoundedStack<T> where T : struct
    {
        private struct Node
        {
            public T Value;
            public int Next;
        }

        private readonly Node[] _nodes;
        private readonly int _capacity;

        // Packed as { index (low 32 bits), tag (high 32 bits) }
        private long _freeHead;
        private long _usedHead;

        private int _count;

        public int Capacity => _capacity;
        public int Count => Volatile.Read(ref _count);
        public int EstimatedJobCount => Count;

        public MpmcBoundedStack(int capacityPowerOfTwo)
        {
            if (capacityPowerOfTwo <= 0 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
                throw new ArgumentException("Capacity must be power of two.", nameof(capacityPowerOfTwo));

            _capacity = capacityPowerOfTwo;
            _nodes = new Node[_capacity];

            // Build free list
            for (int i = 0; i < _capacity - 1; i++)
                _nodes[i].Next = i + 1;

            _nodes[_capacity - 1].Next = -1;

            _freeHead = PackHead(0, 0);
            _usedHead = PackHead(-1, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPush(in T item)
        {
            int index = PopIndex(ref _freeHead);
            if (index < 0)
                return false; // full

            // Write payload normally (struct write)
            _nodes[index].Value = item;

            // Publishing happens via CAS in PushIndex (full fence)
            PushIndex(ref _usedHead, index);

            Interlocked.Increment(ref _count);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPop(out T item)
        {
            int index = PopIndex(ref _usedHead);
            if (index < 0)
            {
                item = default;
                return false; // empty
            }

            // We own the node after CAS → safe to read
            item = _nodes[index].Value;

            PushIndex(ref _freeHead, index);

            Interlocked.Decrement(ref _count);
            return true;
        }

        // -----------------------------
        // Treiber stack primitives
        // -----------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int PopIndex(ref long head)
        {
            SpinWait sw = default;

            while (true)
            {
                long observed = Volatile.Read(ref head);
                UnpackHead(observed, out int index, out int tag);

                if (index < 0)
                    return -1;

                int next = _nodes[index].Next;
                long desired = PackHead(next, tag + 1);

                if (Interlocked.CompareExchange(ref head, desired, observed) == observed)
                    return index;

                sw.SpinOnce();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PushIndex(ref long head, int index)
        {
            SpinWait sw = default;

            while (true)
            {
                long observed = Volatile.Read(ref head);
                UnpackHead(observed, out int current, out int tag);

                _nodes[index].Next = current;
                long desired = PackHead(index, tag + 1);

                if (Interlocked.CompareExchange(ref head, desired, observed) == observed)
                    return;

                sw.SpinOnce();
            }
        }

        // -----------------------------
        // Head packing helpers
        // -----------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long PackHead(int index, int tag)
        {
            unchecked
            {
                return ((long)tag << 32) | (uint)index;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UnpackHead(long packed, out int index, out int tag)
        {
            unchecked
            {
                index = (int)(packed & 0xFFFFFFFF);
                tag = (int)(packed >> 32);
            }
        }
    }
}
