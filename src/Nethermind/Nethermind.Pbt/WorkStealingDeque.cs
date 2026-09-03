// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Threading;

namespace Nethermind.Pbt;

/// <summary>
/// A bounded single-owner work-stealing deque: the owning thread pushes and pops at the head, any
/// other thread steals from the tail.
/// </summary>
/// <remarks>
/// The Chase-Lev/ABP algorithm. The head is the owner's alone, so a push and all but one pop are
/// plain writes over a memory barrier; only the last item can race a thief, and only there does a pop
/// take the same compare-and-swap the tail end always pays. That asymmetry is the point: a producer
/// that mostly consumes its own work never contends, and the contention a thief does cause is
/// confined to the far end of the queue.
/// <para>
/// A steal returns nothing both for an empty queue and for a lost race, and a full queue refuses a
/// push, so a caller cannot read anything into a null result beyond "no item, this time". Both are
/// what let this stay a hint channel: the queue never duplicates or invents an item, but it may
/// decline to hand one over, which the caller must be free to answer by doing the work itself.
/// </para>
/// <para>
/// A slot is not cleared as its item leaves — a thief cannot safely, the array is bounded and the
/// next push around the ring overwrites it — so a stale reference lives until then.
/// </para>
/// </remarks>
internal sealed class WorkStealingDeque<T> where T : class
{
    private readonly T?[] _items;
    private readonly int _mask;

    /// <summary>Where the next push goes; the owner's end, written by nothing else.</summary>
    private CacheLinePaddedLong _head;

    /// <summary>The oldest item, which a thief takes; on its own cache line to keep steals off the owner's.</summary>
    private CacheLinePaddedLong _tail;

    /// <param name="capacity">Slots the queue holds, a power of two.</param>
    public WorkStealingDeque(int capacity)
    {
        if (!BitOperations.IsPow2(capacity)) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The queue capacity must be a power of two");

        _items = new T?[capacity];
        _mask = capacity - 1;
    }

    /// <summary>
    /// Where the owner's end stands: it rises with a push and falls with a pop, so the owner can take it
    /// as a mark before pushing a batch of its own and pop back down to it afterwards.
    /// </summary>
    /// <remarks>Owner-only: a steal moves the tail, never this.</remarks>
    public long Head => _head.Value;

    /// <summary>Pushes <paramref name="item"/>; <c>false</c> when the queue is full. Owner thread only.</summary>
    public bool TryPushHead(T item)
    {
        long head = _head.Value;
        if (head - Volatile.Read(ref _tail.Value) >= _items.Length) return false;

        _items[head & _mask] = item;
        Volatile.Write(ref _head.Value, head + 1);
        return true;
    }

    /// <summary>
    /// Takes the most recently pushed item, or <c>null</c> when the queue is empty or a thief won the
    /// race for its last one. Owner thread only.
    /// </summary>
    public T? TryPopHead()
    {
        long head = _head.Value - 1;

        // claim the slot before reading the tail, so that a thief racing for the same item sees one end
        // move before the other and the compare-and-swap below settles which of the two took it
        Volatile.Write(ref _head.Value, head);
        Interlocked.MemoryBarrier();
        long tail = Volatile.Read(ref _tail.Value);

        if (tail > head)
        {
            Volatile.Write(ref _head.Value, head + 1);
            return null;
        }

        T? item = _items[head & _mask];
        if (tail != head) return item;

        // the queue's last item, which a thief may be taking at this very moment
        bool won = Interlocked.CompareExchange(ref _tail.Value, tail + 1, tail) == tail;
        Volatile.Write(ref _head.Value, head + 1);
        return won ? item : null;
    }

    /// <summary>
    /// Takes the oldest item, or <c>null</c> when the queue is empty or another thread took it first.
    /// Any thread but the owner.
    /// </summary>
    public T? TrySteal()
    {
        long tail = Volatile.Read(ref _tail.Value);
        Interlocked.MemoryBarrier();
        long head = Volatile.Read(ref _head.Value);
        if (tail >= head) return null;

        // read before the claim: past it the owner may push over this very slot
        T? item = _items[tail & _mask];
        return Interlocked.CompareExchange(ref _tail.Value, tail + 1, tail) == tail ? item : null;
    }
}
