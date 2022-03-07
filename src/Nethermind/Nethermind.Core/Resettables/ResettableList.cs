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
using System.Collections;
using System.Collections.Generic;

namespace Nethermind.Core.Resettables;

public class ResettableList<T> : IList<T>
{
    private List<T> _wrapped;
    private readonly int _startCapacity;
    private readonly int _resetRatio;
    private int _currentCapacity;

    public ResettableList(int startCapacity = Resettable.StartCapacity, int resetRatio = Resettable.ResetRatio)
    {
        _wrapped = new List<T>(startCapacity);
        _startCapacity = startCapacity;
        _resetRatio = resetRatio;
        _currentCapacity = _startCapacity;
    }
    
    public IEnumerator<T> GetEnumerator() => _wrapped.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_wrapped).GetEnumerator();

    public void Add(T item) => _wrapped.Add(item);

    public void Clear() => _wrapped.Clear();

    public bool Contains(T item) => _wrapped.Contains(item);

    public void CopyTo(T[] array, int arrayIndex) => _wrapped.CopyTo(array, arrayIndex);

    public bool Remove(T item) => _wrapped.Remove(item);

    public int Count => _wrapped.Count;

    public bool IsReadOnly => false;

    public int Capacity => _wrapped.Capacity;

    public int IndexOf(T item) => _wrapped.IndexOf(item);

    public void Insert(int index, T item) => _wrapped.Insert(index, item);

    public void RemoveAt(int index) => _wrapped.RemoveAt(index);

    public T this[int index]
    {
        get => _wrapped[index];
        set => _wrapped[index] = value;
    }
    
    public void Reset()
    {
        if (_wrapped.Count < _currentCapacity / _resetRatio && _currentCapacity != _startCapacity)
        {
            _currentCapacity = Math.Max(_startCapacity, _currentCapacity / _resetRatio);
            _wrapped.Capacity = _currentCapacity;
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
