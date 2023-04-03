// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Core.Collections
{
    public sealed class ArrayPoolList<T> : IList<T>, IReadOnlyList<T>, IDisposable
    {
        private readonly ArrayPool<T> _arrayPool;
        private T[] _array;
        private int _count = 0;
        private int _capacity;
        private bool _disposed;

        public ArrayPoolList(int capacity) : this(ArrayPool<T>.Shared, capacity)
        {

        }

        public ArrayPoolList(int capacity, IEnumerable<T> enumerable) : this(capacity)
        {
            this.AddRange(enumerable);
        }

        public ArrayPoolList(ArrayPool<T> arrayPool, int capacity)
        {
            _arrayPool = arrayPool;
            _array = arrayPool.Rent(capacity);
            _capacity = _array.Length;
        }

        public IEnumerator<T> GetEnumerator()
        {
            GuardDispose();
            return new ArrayPoolListEnumerator(_array, _count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GuardDispose()
        {
            if (_disposed)
            {
                ThrowObjectDisposed();
            }

            [DoesNotReturn]
            static void ThrowObjectDisposed()
            {
                throw new ObjectDisposedException(nameof(ArrayPoolList<T>));
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Add(T item)
        {
            GuardResize();
            _array[_count++] = item;
        }

        public void AddRange(Span<T> items)
        {
            GuardResize(items.Length);
            items.CopyTo(_array.AsSpan(_count, items.Length));
            _count += items.Length;
        }

        public void Clear()
        {
            _count = 0;
        }

        public bool Contains(T item)
        {
            GuardDispose();
            int indexOf = Array.IndexOf(_array, item);
            return indexOf >= 0 && indexOf < _count;
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            GuardDispose();
            _array.AsMemory(0, _count).CopyTo(array.AsMemory(arrayIndex));
        }

        public int Count => _count;

        public int Capacity => _capacity;

        public bool IsReadOnly => false;

        public int IndexOf(T item)
        {
            GuardDispose();
            int indexOf = Array.IndexOf(_array, item);
            return indexOf < _count ? indexOf : -1;
        }

        public void Insert(int index, T item)
        {
            GuardResize();
            GuardIndex(index, allowEqualToCount: true);
            _array.AsMemory(index, _count - index).CopyTo(_array.AsMemory(index + 1));
            _array[index] = item;
            _count++;
        }

        private void GuardResize(int itemsToAdd = 1)
        {
            GuardDispose();
            int newCount = _count + itemsToAdd;
            if (newCount > _capacity)
            {
                int newCapacity = _capacity * 2;
                while (newCount > newCapacity)
                {
                    newCapacity *= 2;
                }
                T[] newArray = _arrayPool.Rent(newCapacity);
                _array.CopyTo(newArray, 0);
                T[] oldArray = Interlocked.Exchange(ref _array, newArray);
                _capacity = newArray.Length;
                _arrayPool.Return(oldArray);
            }
        }

        public bool Remove(T item) => RemoveAtInternal(IndexOf(item), false);
        public void RemoveAt(int index) => RemoveAtInternal(index, true);
        private bool RemoveAtInternal(int index, bool shouldThrow)
        {
            bool isValid = GuardIndex(index, shouldThrow);
            if (isValid)
            {
                int start = index + 1;
                if (start < _count)
                {
                    _array.AsMemory(start, _count - index).CopyTo(_array.AsMemory(index));
                }

                _count--;
            }

            return isValid;
        }

        public T this[int index]
        {
            get
            {
                GuardIndex(index);
                return _array[index];
            }
            set
            {
                GuardIndex(index);
                _array[index] = value;
            }
        }

        private bool GuardIndex(int index, bool shouldThrow = true, bool allowEqualToCount = false)
        {
            GuardDispose();
            int count = _count;
            if ((uint)index > (uint)count || (!allowEqualToCount && index == count))
            {
                if (shouldThrow)
                {
                    ThrowArgumentOutOfRangeException();
                }
                return false;
            }

            return true;

            [DoesNotReturn]
            static void ThrowArgumentOutOfRangeException()
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        private struct ArrayPoolListEnumerator : IEnumerator<T>
        {
            private readonly T[] _array;
            private readonly int _count;
            private int _index;

            public ArrayPoolListEnumerator(T[] array, int count)
            {
                _array = array;
                _count = count;
                _index = -1;
            }

            public bool MoveNext() => ++_index < _count;

            public void Reset() => _index = -1;

            public T Current => _array[_index];

            object IEnumerator.Current => Current!;

            public void Dispose() { }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _arrayPool.Return(_array);
                _disposed = true;
            }
        }

        public Span<T> AsSpan() => _array.AsSpan(0, _count);
    }
}
