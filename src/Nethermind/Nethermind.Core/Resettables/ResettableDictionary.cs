// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;

namespace Nethermind.Core.Resettables
{
    public class ResettableDictionary<TKey, TValue> : IDictionary<TKey, TValue> where TKey : notnull
    {
        private readonly IEqualityComparer<TKey>? _comparer;
        private int _currentCapacity;
        private readonly int _startCapacity;
        private readonly int _resetRatio;

        private Dictionary<TKey, TValue> _wrapped;

        public ResettableDictionary(
            IEqualityComparer<TKey>? comparer,
            int startCapacity = Resettable.StartCapacity,
            int resetRatio = Resettable.ResetRatio)
        {
            _comparer = comparer;
            _wrapped = new Dictionary<TKey, TValue>(startCapacity, _comparer);
            _startCapacity = startCapacity;
            _resetRatio = resetRatio;
            _currentCapacity = _startCapacity;
        }

        public ResettableDictionary(
            int startCapacity = Resettable.StartCapacity,
            int resetRatio = Resettable.ResetRatio)
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
            _wrapped.Add(item.Key, item.Value);
        }

        public void Clear()
        {
            _wrapped.Clear();
        }

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return ((IDictionary<TKey, TValue>)_wrapped).Contains(item);
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            ((IDictionary<TKey, TValue>)_wrapped).CopyTo(array, arrayIndex);
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            return ((IDictionary<TKey, TValue>)_wrapped).Remove(item);
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
#pragma warning disable 8601
            // fixed C# 9
            return _wrapped.TryGetValue(key, out value);
#pragma warning restore 8601
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
                _wrapped = new Dictionary<TKey, TValue>(_currentCapacity, _comparer);
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
