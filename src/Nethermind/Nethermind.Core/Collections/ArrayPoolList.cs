//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Collections
{
    public class ArrayPoolList<T> : IList<T>, IDisposable
    {
        private readonly ArrayPool<T> _arrayPool;
        private T[] _array;
        private int _count = 0;
        private int _capacity;
        private bool _disposed;

        public ArrayPoolList(int capacity) : this(ArrayPool<T>.Shared, capacity)
        {
            
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
            if (_disposed) throw new ObjectDisposedException(nameof(ArrayPoolList<T>));
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

        private void GuardResize()
        {
            GuardDispose();
            if (_count == _capacity)
            {
                int newCapacity = _capacity * 2;
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
            if (index < 0)
            {
                return shouldThrow
                    ? throw new ArgumentOutOfRangeException($"Index {index} is below 0.")
                    : false;
            }
            else if (index >= _count && (!allowEqualToCount || index > _count))
            {
                return shouldThrow
                    ? throw new ArgumentOutOfRangeException($"Index {index} is above count {_count}.")
                    : false;
            }

            return true;
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
    }
}
