//  Copyright (c) 2018 Demerzel Solutions Limited
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

namespace Nethermind.Store
{
    public class ResettableDictionary<TKey, TValue> : IDictionary<TKey, TValue>
    {
        private int _currentCapacity;
        private int _startCapacity;
        private int _resetRatio;

        private IDictionary<TKey, TValue> _wrapped;

        public ResettableDictionary(IEqualityComparer<TKey> comparer, int startCapacity = Resettable.StartCapacity, int resetRatio = Resettable.ResetRatio)
        {
            _wrapped = new Dictionary<TKey, TValue>(startCapacity, comparer);
            _startCapacity = startCapacity;
            _resetRatio = resetRatio;
            _currentCapacity = _startCapacity;
        }

        public ResettableDictionary(int startCapacity = Resettable.StartCapacity, int resetRatio = Resettable.ResetRatio)
            : this(null, startCapacity, resetRatio)
        {
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return _wrapped.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            _wrapped.Add(item);
        }

        public void Clear()
        {
            _wrapped.Clear();
        }

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return _wrapped.Contains(item);
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            _wrapped.CopyTo(array, arrayIndex);
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            return _wrapped.Remove(item);
        }

        public int Count => _wrapped.Count;
        public bool IsReadOnly => false;

        public void Add(TKey key, TValue value)
        {
            _wrapped.Add(key, value);
        }

        public bool ContainsKey(TKey key)
        {
            return _wrapped.ContainsKey(key);
        }

        public bool Remove(TKey key)
        {
            return _wrapped.Remove(key);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            return _wrapped.TryGetValue(key, out value);
        }

        public TValue this[TKey key]
        {
            get => _wrapped[key];
            set => _wrapped[key] = value;
        }

        public ICollection<TKey> Keys => _wrapped.Keys;
        public ICollection<TValue> Values => _wrapped.Values;

        public void Reset()
        {
            if (_wrapped.Count == 0)
            {
                return;
            }

            if (_wrapped.Count < _currentCapacity / _resetRatio && _currentCapacity != _startCapacity)
            {
                _currentCapacity = Math.Max(_startCapacity, _currentCapacity / _resetRatio);
                _wrapped = new Dictionary<TKey, TValue>(_currentCapacity);
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