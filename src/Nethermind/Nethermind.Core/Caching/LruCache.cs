// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Caching;

public sealed class LruCache<TKey, TValue> : ICache<TKey, TValue> where TKey : notnull
{
    private readonly int _maxCapacity;
    private readonly Dictionary<TKey, LinkedListNode<LruCacheItem>> _cacheMap;
    private LinkedListNode<LruCacheItem>? _first;

    public LruCache(int maxCapacity, int startCapacity, string name)
    {
        if (maxCapacity < 1)
        {
            throw new ArgumentOutOfRangeException();
        }

        _maxCapacity = maxCapacity;
        _cacheMap = typeof(TKey) == typeof(byte[])
            ? new Dictionary<TKey, LinkedListNode<LruCacheItem>>((IEqualityComparer<TKey>)Bytes.EqualityComparer)
            : new Dictionary<TKey, LinkedListNode<LruCacheItem>>(startCapacity); // do not initialize it at the full capacity
    }

    public LruCache(int maxCapacity, string name)
        : this(maxCapacity, 0, name)
    {
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Clear()
    {
        _first = null;
        _cacheMap.Clear();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public TValue Get(TKey key)
    {
        if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
        {
            TValue value = node.Value.Value;
            MoveToLast(node);
            return value;
        }

#pragma warning disable 8603
        // fixed C# 9
        return default;
#pragma warning restore 8603
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryGet(TKey key, out TValue value)
    {
        if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
        {
            value = node.Value.Value;
            MoveToLast(node);
            return true;
        }

#pragma warning disable 8601
        // fixed C# 9
        value = default;
#pragma warning restore 8601
        return false;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool Set(TKey key, TValue val)
    {
        if (val is null)
        {
            return Delete(key);
        }

        if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
        {
            node.Value.Value = val;
            MoveToLast(node);
            return false;
        }
        else
        {
            if (_cacheMap.Count >= _maxCapacity)
            {
                Replace(key, val);
            }
            else
            {
                LinkedListNode<LruCacheItem> newNode = new(new(key, val));
                AddLast(newNode);
                _cacheMap.Add(key, newNode);
            }

            return true;
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool Delete(TKey key)
    {
        if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
        {
            Remove(node);
            _cacheMap.Remove(key);
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool Contains(TKey key) => _cacheMap.ContainsKey(key);

    [MethodImpl(MethodImplOptions.Synchronized)]
    public IDictionary<TKey, TValue> Clone() => _cacheMap.ToDictionary(i => i.Key, i => i.Value.Value.Value);

    private void Replace(TKey key, TValue value)
    {
        LinkedListNode<LruCacheItem>? node = _first;
        if (node is null)
        {
            throw new InvalidOperationException(
                $"{nameof(LruCache<TKey, TValue>)} called {nameof(Replace)} when empty.");
        }

        Remove(node);
        _cacheMap.Remove(node!.Value.Key);

        node.Value = new(key, value);
        AddLast(node);
        _cacheMap.Add(key, node);
    }

    private void MoveToLast(LinkedListNode<LruCacheItem> node)
    {
        if (node.Next == node)
        {
            Debug.Assert(_cacheMap.Count == 1 && _first == node, "this should only be true for a list with only one node");
            // Do nothing only one node
        }
        else
        {
            Remove(node);
            AddLast(node);
        }
    }

    private void AddLast(LinkedListNode<LruCacheItem> node)
    {
        if (_first is null)
        {
            SetFirst(node);
        }
        else
        {
            InsertLast(node);
        }
    }

    private void InsertLast(LinkedListNode<LruCacheItem> newNode)
    {
        LinkedListNode<LruCacheItem> first = _first!;
        newNode.Next = first;
        newNode.Prev = first.Prev;
        first.Prev!.Next = newNode;
        first.Prev = newNode;
    }

    private void SetFirst(LinkedListNode<LruCacheItem> newNode)
    {
        Debug.Assert(_first is null && _cacheMap.Count == 0, "LinkedList must be empty when this method is called!");
        newNode.Next = newNode;
        newNode.Prev = newNode;
        _first = newNode;
    }

    private void Remove(LinkedListNode<LruCacheItem> node)
    {
        Debug.Assert(_first is not null, "This method shouldn't be called on empty list!");
        if (node.Next == node)
        {
            Debug.Assert(_cacheMap.Count == 1 && _first == node, "this should only be true for a list with only one node");
            _first = null;
        }
        else
        {
            node.Next!.Prev = node.Prev;
            node.Prev!.Next = node.Next;
            if (_first == node)
            {
                _first = node.Next;
            }
        }
    }

    private struct LruCacheItem
    {
        public LruCacheItem(TKey k, TValue v)
        {
            Key = k;
            Value = v;
        }

        public readonly TKey Key;
        public TValue Value;
    }

    public long MemorySize => CalculateMemorySize(0, _cacheMap.Count);

    public static long CalculateMemorySize(int keyPlusValueSize, int currentItemsCount)
    {
        // it may actually be different if the initial capacity not equal to max (depending on the dictionary growth path)

        const int preInit = 48 /* LinkedList */ + 80 /* Dictionary */ + 24;
        int postInit = 52 /* lazy init of two internal dictionary arrays + dictionary size times (entry size + int) */ + MemorySizes.FindNextPrime(currentItemsCount) * 28 + currentItemsCount * 80 /* LinkedListNode and CacheItem times items count */;
        return MemorySizes.Align(preInit + postInit + keyPlusValueSize * currentItemsCount);
    }
}
