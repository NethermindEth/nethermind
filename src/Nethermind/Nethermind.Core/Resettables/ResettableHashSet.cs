// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;

namespace Nethermind.Core.Resettables
{
    public class ResettableHashSet<T> : ICollection<T>, IReadOnlyCollection<T>
    {
        private int _currentCapacity;
        private readonly int _startCapacity;
        private readonly int _resetRatio;

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
