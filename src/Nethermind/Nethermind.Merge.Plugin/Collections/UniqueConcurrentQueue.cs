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
using System.Collections.Concurrent;
using System.Collections.Generic;
using ConcurrentCollections;

namespace Nethermind.Merge.Plugin.Collections;

public class UniqueConcurrentQueue<T> : IProducerConsumerCollection<T>
{
    private readonly ConcurrentHashSet<T> _concurrentHashSet = new();
    private readonly ConcurrentQueue<T> _concurrentQueue = new();

    public IEnumerator<T> GetEnumerator() => _concurrentQueue.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void CopyTo(Array array, int index) => ((ICollection)_concurrentQueue).CopyTo(array, index);
    public int Count => _concurrentQueue.Count;
    public bool IsSynchronized => ((ICollection)_concurrentQueue).IsSynchronized;
    public object SyncRoot => ((ICollection)_concurrentQueue).SyncRoot;
    public void CopyTo(T[] array, int index) => _concurrentQueue.CopyTo(array, index);
    public T[] ToArray() => _concurrentQueue.ToArray();
    
    public bool Contains(T item) => _concurrentHashSet.Contains(item);

    public bool TryAdd(T item)
    {
        if (_concurrentHashSet.Add(item))
        {
            _concurrentQueue.Enqueue(item);
        }
        
        return true;
    }

    public bool TryTake(out T item)
    {
        if (_concurrentQueue.TryDequeue(out item!))
        {
            _concurrentHashSet.TryRemove(item);
        }

        return true;
    }
}
