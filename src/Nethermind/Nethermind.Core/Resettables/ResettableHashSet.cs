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

using System;
using System.Collections;
using System.Collections.Generic;

namespace Nethermind.Core.Resettables
{
    public class ResettableHashSet<T> : ICollection<T>, IReadOnlyCollection<T>
    {
        private int _currentCapacity;
        private int _startCapacity;
        private int _resetRatio;

        private HashSet<T> _wrapped;

        public ResettableHashSet(int startCapacity = Resettable.StartCapacity, int resetRatio = Resettable.ResetRatio)
        {
            _wrapped = new HashSet<T>(startCapacity);
            _startCapacity = startCapacity;
            _resetRatio = resetRatio;
            _currentCapacity = _startCapacity;
        }

        public IEnumerator<T> GetEnumerator()
        {
            return _wrapped.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Add(T item)
        {
            _wrapped.Add(item);
        }

        public void Clear()
        {
            _wrapped.Clear();
        }

        public bool Contains(T item)
        {
            return _wrapped.Contains(item);
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            _wrapped.CopyTo(array, arrayIndex);
        }

        public bool Remove(T item)
        {
            return _wrapped.Remove(item);
        }

        public int Count => _wrapped.Count;
        public bool IsReadOnly => false;

        public void Reset()
        {
            if (_wrapped.Count == 0)
            {
                return;
            }
            
            if (_wrapped.Count < _currentCapacity / _resetRatio && _currentCapacity != _startCapacity)
            {
                _currentCapacity = Math.Max(_startCapacity, _currentCapacity / _resetRatio);
                _wrapped = new HashSet<T>(_currentCapacity);
            }
            else
            {
                while (_wrapped.Count > _currentCapacity)
                {
                    _currentCapacity *= _resetRatio;
                }
                
                _wrapped.Clear();
            }
        }
    }
}
