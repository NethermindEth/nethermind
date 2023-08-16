// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Verkle.Tree;

/// <summary>
/// A simple of generic objects.
/// This structure implements the functionality of
/// both queue and stack in a single data structure.
/// 1. Queue: Enqueue(), Dequeue(), FIFO Enumerator
/// 2. Stack: Pop(), LIFO Enumerator
/// Internally it is implemented as a circular buffer
/// and the size of buffer is fixed during initialization
/// so Enqueue and Dequeue is O(1)
/// </summary>
/// <typeparam name="T"></typeparam>
[DebuggerDisplay("Count = {Count}")]
[Serializable]
public class StackQueue<T>
{
    public T[] _array;
    private int _head;       // The index from which to dequeue if the queue isn't empty.
    private int _tail;       // The index at which to enqueue if the queue isn't full.
    private int _version;

    // Creates a queue with room for capacity objects. Capacity cannot be changed after initialization
    public StackQueue(int capacity)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "NeedNonNegNum");
        _array = new T[capacity];
    }

    public int Count { get; private set; }

    public void Clear()
    {
        if (Count != 0)
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                if (_head < _tail)
                {
                    Array.Clear(_array, _head, Count);
                }
                else
                {
                    Array.Clear(_array, _head, _array.Length - _head);
                    Array.Clear(_array, 0, _tail);
                }
            }

            Count = 0;
        }

        _head = 0;
        _tail = 0;
        _version++;
    }

    // Adds item to the tail of the queue. Throws if the queue if full
    public void Enqueue(T item)
    {
        if (Count == _array.Length)
        {
            throw new ArgumentException($"QueueIsFull: {Count}");
        }

        _array[_tail] = item;
        MoveNext(ref _tail);
        Count++;
        _version++;
    }

    // Adds item to the tail of the queue. If the queue if full, it will perform a dequeue
    // and then insert the new element. It also returns the dequeued element
    public bool EnqueueAndReplaceIfFull(T item, [MaybeNullWhen(true)]out T element)
    {
        if (Count == _array.Length)
        {
            element = Dequeue();
            Enqueue(item);
            return false;
        }

        _array[_tail] = item;
        MoveNext(ref _tail);
        Count++;
        element = default;
        _version++;
        return true;
    }

    // GetQueueEnumerator returns an IEnumerator over this StackQueue. This
    // enumerator returns elements in FIFO order.
    // This Enumerator will support removing.
    public QueueEnumerator GetQueueEnumerator()
    {
        return new QueueEnumerator(this);
    }

    // GetQueueEnumerator returns an IEnumerator over this StackQueue. This
    // enumerator returns elements int LIFO order.
    // This Enumerator will support removing.
    public StackEnumerator GetStackEnumerator()
    {
        return new StackEnumerator(this);
    }

    // Removes the object at the head of the queue and returns it. If the queue
    // is empty, this method throws an
    // InvalidOperationException.
    public T Dequeue()
    {
        int head = _head;
        T[] array = _array;

        if (Count == 0)
        {
            ThrowForEmptyQueue();
        }

        T removed = array[head];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            array[head] = default!;
        }
        MoveNext(ref _head);
        Count--;
        _version++;
        return removed;
    }

    public bool TryDequeue([MaybeNullWhen(false)] out T result)
    {
        int head = _head;
        T[] array = _array;

        if (Count == 0)
        {
            result = default;
            return false;
        }

        result = array[head];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            array[head] = default!;
        }
        MoveNext(ref _head);
        Count--;
        _version++;
        return true;
    }

    // Returns the object at the head of the queue. The object remains in the
    // queue. If the queue is empty, this method throws an
    // InvalidOperationException.
    public T Peek()
    {
        if (Count == 0)
        {
            ThrowForEmptyQueue();
        }

        return _array[_head];
    }

    public bool TryPeek([MaybeNullWhen(false)] out T result)
    {
        if (Count == 0)
        {
            result = default;
            return false;
        }

        result = _array[_head];
        return true;
    }

    // Remove the last inserted element from the queue
    public void Pop(out T element)
    {
        if (Count == 0)
        {
            ThrowForEmptyQueue();
        }

        MoveBack(ref _tail);
        Count--;
        element = _array[_tail];
    }

    public bool TryPop([MaybeNullWhen(false)]out T element)
    {
        if (Count == 0)
        {
            element = default;
            return false;
        }

        MoveBack(ref _tail);
        Count--;
        element = _array[_tail];
        return true;
    }

    // Returns the object at the head of the stack. The object remains in the
    // stack. If the stack is empty, this method throws an
    // InvalidOperationException.
    public T PeekStack()
    {
        if (Count == 0)
        {
            ThrowForEmptyQueue();
        }

        int tmp = _tail - 1;
        if (tmp == -1)
        {
            tmp = _array.Length - 1;
        }

        return _array[tmp];
    }

    public bool TryPeekStack([MaybeNullWhen(false)] out T result)
    {
        if (Count == 0)
        {
            result = default;
            return false;
        }

        int tmp = _tail - 1;
        if (tmp == -1)
        {
            tmp = _array.Length - 1;
        }
        result = _array[tmp];
        return true;
    }

    // Returns true if the queue contains at least one object equal to item.
    // Equality is determined using EqualityComparer<T>.Default.Equals().
    public bool Contains(T item)
    {
        if (Count == 0)
        {
            return false;
        }

        if (_head < _tail)
        {
            return Array.IndexOf(_array, item, _head, Count) >= 0;
        }

        // We've wrapped around. Check both partitions, the least recently enqueued first.
        return
            Array.IndexOf(_array, item, _head, _array.Length - _head) >= 0 ||
            Array.IndexOf(_array, item, 0, _tail) >= 0;
    }

    // Iterates over the objects in the queue, returning an array of the
    // objects in the StackQueue, or an empty array if the queue is empty.
    // The order of elements in the array is first in to last in, the same
    // order produced by successive calls to Dequeue.
    public T[] ToArray()
    {
        if (Count == 0)
        {
            return Array.Empty<T>();
        }

        T[] arr = new T[Count];

        if (_head < _tail)
        {
            Array.Copy(_array, _head, arr, 0, Count);
        }
        else
        {
            Array.Copy(_array, _head, arr, 0, _array.Length - _head);
            Array.Copy(_array, 0, arr, _array.Length - _head, _tail);
        }

        return arr;
    }

    // Increments the index wrapping it if necessary.
    private void MoveNext(ref int index)
    {
        // It is tempting to use the remainder operator here but it is actually much slower
        // than a simple comparison and a rarely taken branch.
        // JIT produces better code than with ternary operator ?:
        int tmp = index + 1;
        if (tmp == _array.Length)
        {
            tmp = 0;
        }
        index = tmp;
    }

    private void MoveBack(ref int index)
    {
        // It is tempting to use the remainder operator here but it is actually much slower
        // than a simple comparison and a rarely taken branch.
        // JIT produces better code than with ternary operator ?:
        int tmp = index - 1;
        if (tmp == -1)
        {
            tmp = _array.Length - 1;
        }
        index = tmp;
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);

        if (arrayIndex < 0 || arrayIndex > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(arrayIndex), arrayIndex, "IndexMustBeLessOrEqual");
        }

        if (array.Length - arrayIndex < Count)
        {
            throw new ArgumentException("InvalidOffLen");
        }

        int numToCopy = Count;
        if (numToCopy == 0) return;

        int firstPart = Math.Min(_array.Length - _head, numToCopy);
        Array.Copy(_array, _head, array, arrayIndex, firstPart);
        numToCopy -= firstPart;
        if (numToCopy > 0)
        {
            Array.Copy(_array, 0, array, arrayIndex + _array.Length - _head, numToCopy);
        }
    }

    void CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);

        if (array.Rank != 1)
        {
            throw new ArgumentException("RankMultiDimNotSupported", nameof(array));
        }

        if (array.GetLowerBound(0) != 0)
        {
            throw new ArgumentException("NonZeroLowerBound", nameof(array));
        }

        int arrayLen = array.Length;
        if (index < 0 || index > arrayLen)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "IndexMustBeLessOrEqual");
        }

        if (arrayLen - index < Count)
        {
            throw new ArgumentException("InvalidOffLen");
        }

        int numToCopy = Count;
        if (numToCopy == 0) return;

        try
        {
            int firstPart = (_array.Length - _head < numToCopy) ? _array.Length - _head : numToCopy;
            Array.Copy(_array, _head, array, index, firstPart);
            numToCopy -= firstPart;

            if (numToCopy > 0)
            {
                Array.Copy(_array, 0, array, index + _array.Length - _head, numToCopy);
            }
        }
        catch (ArrayTypeMismatchException)
        {
            throw new ArgumentException("InvalidArrayType", nameof(array));
        }
    }

    private void ThrowForEmptyQueue()
    {
        Debug.Assert(Count == 0);
        throw new InvalidOperationException("EmptyQueue");
    }

    // Implements an enumerator for a StackQueue.  The enumerator uses the
    // internal version number of the list to ensure that no modifications are
    // made to the list while an enumeration is in progress.
    public struct QueueEnumerator : IEnumerator<T>
    {
        private const int Started = -1;
        private const int Ended = -2;
        private readonly int Completed { get; init; }

        private readonly StackQueue<T> _q;
        private readonly int _version;
        private int _index;   // -1 = not started, -2 = ended/disposed
        private T? _currentElement;

        internal QueueEnumerator(StackQueue<T> q)
        {
            _q = q;
            _version = q._version;
            _index = Started;
            _currentElement = default;
            Completed = _q.Count;
        }

        public void Dispose()
        {
            _index = Ended;
            _currentElement = default;
        }

        public bool MoveNext()
        {
            if (_version != _q._version) throw new InvalidOperationException("EnumFailedVersion");

            if (_index == Ended)
                return false;

            _index++;

            if (_index == Completed)
            {
                // We've run past the last element
                _index = Ended;
                _currentElement = default;
                return false;
            }

            // Cache some fields in locals to decrease code size
            T[] array = _q._array;
            int capacity = array.Length;

            // _index represents the 0-based index into the queue, however the queue
            // doesn't have to start from 0 and it may not even be stored contiguously in memory.

            int arrayIndex = _q._head + _index; // this is the actual index into the queue's backing array
            if (arrayIndex >= capacity)
            {
                // NOTE: Originally we were using the modulo operator here, however
                // on Intel processors it has a very high instruction latency which
                // was slowing down the loop quite a bit.
                // Replacing it with simple comparison/subtraction operations sped up
                // the average foreach loop by 2x.

                arrayIndex -= capacity; // wrap around if needed
            }

            _currentElement = array[arrayIndex];
            return true;
        }

        public T Current
        {
            get
            {
                if (_index < 0)
                    ThrowEnumerationNotStartedOrEnded();
                return _currentElement!;
            }
        }

        private void ThrowEnumerationNotStartedOrEnded()
        {
            Debug.Assert(_index == Started || _index == Ended);
            throw new InvalidOperationException(_index == Started ? "EnumNotStarted" : "EnumEnded");
        }

        object? IEnumerator.Current
        {
            get { return Current; }
        }

        void IEnumerator.Reset()
        {
            if (_version != _q._version) throw new InvalidOperationException("EnumFailedVersion");
            _index = Started;
            _currentElement = default;
        }
    }

    // Implements an enumerator for a StackQueue.  The enumerator uses the
    // internal version number of the list to ensure that no modifications are
    // made to the list while an enumeration is in progress.
    public struct StackEnumerator : IEnumerator<T>
    {
        private readonly int Started { get; init; }
        private readonly int Ended { get; init; }
        private const int Completed = -1;

        private readonly StackQueue<T> _q;
        private readonly int _version;
        private int _index;   // -1 = not started, -2 = ended/disposed
        private T? _currentElement;

        internal StackEnumerator(StackQueue<T> q)
        {
            _q = q;
            _version = _q._version;
            _index = Started = _q.Count;
            _currentElement = default;
            Ended = _q.Count + 1;
        }

        public void Dispose()
        {
            _index = Ended;
            _currentElement = default;
        }

        public bool MoveNext()
        {
            if (_version != _q._version) throw new InvalidOperationException("EnumFailedVersion");

            if (_index == Ended)
                return false;

            _index--;

            if (_index == Completed)
            {
                // We've run past the last element
                _index = Ended;
                _currentElement = default;
                return false;
            }

            // Cache some fields in locals to decrease code size
            T[] array = _q._array;
            int capacity = array.Length;

            // _index represents the 0-based index into the queue, however the queue
            // doesn't have to start from 0 and it may not even be stored contiguously in memory.

            int arrayIndex = _q._head + _index; // this is the actual index into the queue's backing array
            if (arrayIndex >= capacity)
            {
                // NOTE: Originally we were using the modulo operator here, however
                // on Intel processors it has a very high instruction latency which
                // was slowing down the loop quite a bit.
                // Replacing it with simple comparison/subtraction operations sped up
                // the average foreach loop by 2x.

                arrayIndex -= capacity; // wrap around if needed
            }

            _currentElement = array[arrayIndex];
            return true;
        }

        public T Current
        {
            get
            {
                if (_index < 0)
                    ThrowEnumerationNotStartedOrEnded();
                return _currentElement!;
            }
        }

        private void ThrowEnumerationNotStartedOrEnded()
        {
            Debug.Assert(_index == Started || _index == Ended);
            throw new InvalidOperationException(_index == Started ? "EnumNotStarted" : "EnumEnded");
        }

        object? IEnumerator.Current
        {
            get { return Current; }
        }

        void IEnumerator.Reset()
        {
            if (_version != _q._version) throw new InvalidOperationException("EnumFailedVersion");
            _index = Started;
            _currentElement = default;
        }
    }

    /// <summary>Converts an enumerable to an array using the same logic as List{T}.</summary>
    /// <param name="source">The enumerable to convert.</param>
    /// <param name="length">The number of items stored in the resulting array, 0-indexed.</param>
    /// <returns>
    /// The resulting array.  The length of the array may be greater than <paramref name="length"/>,
    /// which is the actual number of elements in the array.
    /// </returns>
    internal static T[] ToArray<T>(IEnumerable<T> source, out int length)
    {
        if (source is ICollection<T> ic)
        {
            int count = ic.Count;
            if (count != 0)
            {
                // Allocate an array of the desired size, then copy the elements into it. Note that this has the same
                // issue regarding concurrency as other existing collections like List<T>. If the collection size
                // concurrently changes between the array allocation and the CopyTo, we could end up either getting an
                // exception from overrunning the array (if the size went up) or we could end up not filling as many
                // items as 'count' suggests (if the size went down).  This is only an issue for concurrent collections
                // that implement ICollection<T>, which as of .NET 4.6 is just ConcurrentDictionary<TKey, TValue>.
                T[] arr = new T[count];
                ic.CopyTo(arr, 0);
                length = count;
                return arr;
            }
        }
        else
        {
            using IEnumerator<T> en = source.GetEnumerator();
            if (en.MoveNext())
            {
                const int DefaultCapacity = 4;
                T[] arr = new T[DefaultCapacity];
                arr[0] = en.Current;
                int count = 1;

                while (en.MoveNext())
                {
                    if (count == arr.Length)
                    {
                        // This is the same growth logic as in List<T>:
                        // If the array is currently empty, we make it a default size.  Otherwise, we attempt to
                        // double the size of the array.  Doubling will overflow once the size of the array reaches
                        // 2^30, since doubling to 2^31 is 1 larger than Int32.MaxValue.  In that case, we instead
                        // constrain the length to be Array.MaxLength (this overflow check works because of the
                        // cast to uint).
                        int newLength = count << 1;
                        if ((uint)newLength > Array.MaxLength)
                        {
                            newLength = Array.MaxLength <= count ? count + 1 : Array.MaxLength;
                        }

                        Array.Resize(ref arr, newLength);
                    }

                    arr[count++] = en.Current;
                }

                length = count;
                return arr;
            }
        }

        length = 0;
        return Array.Empty<T>();
    }
}
