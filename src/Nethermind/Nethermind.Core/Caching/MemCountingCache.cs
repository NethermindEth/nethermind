// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Caching;

public sealed class MemCountingCache : ICache<KeccakKey, byte[]>
{
    private readonly int _maxCapacity;
    private readonly Dictionary<KeccakKey, LinkedListNode<LruCacheItem>> _cacheMap;
    private LinkedListNode<LruCacheItem>? _leastRecentlyUsed;

    private const int PreInitMemorySize =
        80 /* Dictionary */ +
        MemorySizes.SmallObjectOverhead +
        MemorySizes.SmallObjectOverhead +
        8 /* sizeof(int) aligned */;

    private const int PostInitMemorySize =
        52 /* lazy loaded dictionary.Items */ + PreInitMemorySize;

    private const int DictionaryItemSize = 28;
    private int _currentDictionaryCapacity;

    public void Clear()
    {
        _leastRecentlyUsed = null;
        _cacheMap?.Clear();
    }

    public MemCountingCache(int maxCapacity, int startCapacity, string name)
    {
        _maxCapacity = maxCapacity;
        _cacheMap = new(startCapacity); // do not initialize it at the full capacity
    }

    public MemCountingCache(int maxCapacity, string name)
        : this(maxCapacity, 0, name)
    {
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public byte[]? Get(KeccakKey key)
    {
        if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
        {
            byte[] value = node.Value.Value;
            MoveToMostRecent(node);
            return value;
        }

        return default;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool TryGet(KeccakKey key, out byte[]? value)
    {
        if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
        {
            value = node.Value.Value;
            MoveToMostRecent(node);
            return true;
        }

        value = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool Set(KeccakKey key, byte[]? val)
    {
        if (val is null)
        {
            return Delete(key);
        }

        if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
        {
            node.Value.Value = val;
            MoveToMostRecent(node);
            return false;
        }
        else
        {
            long cacheItemMemory = LruCacheItem.FindMemorySize(val);
            int newCount = _cacheMap.Count + 1;
            int capacityRemembered = _currentDictionaryCapacity;
            long dictionaryNewMemory = CalculateDictionaryPartMemory(_currentDictionaryCapacity, newCount);
            int initialGrowth = newCount == 1 ? PostInitMemorySize - PreInitMemorySize : 0;
            long newMemorySize =
                MemorySizes.Align(
                    MemorySize +
                    initialGrowth +
                    dictionaryNewMemory +
                    cacheItemMemory
                );

            if (newMemorySize <= _maxCapacity)
            {
                MemorySize = newMemorySize;
                LinkedListNode<LruCacheItem> newNode = new(new(key, val));
                AddMostRecent(newNode);
                _cacheMap.Add(key, newNode);
            }
            else
            {
                _currentDictionaryCapacity = capacityRemembered;
                Replace(key, val);
            }

            return true;
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool Delete(KeccakKey key)
    {
        if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
        {
            MemorySize -= node.Value.MemorySize;
            Remove(node);
            _cacheMap.Remove(key);

            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool Contains(KeccakKey key) => _cacheMap.ContainsKey(key);

    private void Replace(KeccakKey key, byte[] value)
    {
        LinkedListNode<LruCacheItem> node = _leastRecentlyUsed!;
        Debug.Assert(node != null);

        // ReSharper disable once PossibleNullReferenceException
        MemorySize += MemorySizes.Align(value.Length) - MemorySizes.Align(node.Value.Value.Length);

        _cacheMap.Remove(node!.Value.Key);

        node.Value = new(key, value);
        MoveToMostRecent(node);
        _cacheMap.Add(key, node);
    }

    private void MoveToMostRecent(LinkedListNode<LruCacheItem> node)
    {
        if (node.Next == node)
        {
            Debug.Assert(_cacheMap.Count == 1 && _leastRecentlyUsed == node, "this should only be true for a list with only one node");
            // Do nothing only one node
        }
        else
        {
            Remove(node);
            AddMostRecent(node);
        }
    }

    private void AddMostRecent(LinkedListNode<LruCacheItem> node)
    {
        if (_leastRecentlyUsed is null)
        {
            SetFirst(node);
        }
        else
        {
            InsertMostRecent(node);
        }
    }

    private void InsertMostRecent(LinkedListNode<LruCacheItem> newNode)
    {
        LinkedListNode<LruCacheItem> first = _leastRecentlyUsed!;
        newNode.Next = first;
        newNode.Prev = first.Prev;
        first.Prev!.Next = newNode;
        first.Prev = newNode;
    }

    private void SetFirst(LinkedListNode<LruCacheItem> newNode)
    {
        Debug.Assert(_leastRecentlyUsed is null && _cacheMap.Count == 0, "LinkedList must be empty when this method is called!");
        newNode.Next = newNode;
        newNode.Prev = newNode;
        _leastRecentlyUsed = newNode;
    }

    private void Remove(LinkedListNode<LruCacheItem> node)
    {
        Debug.Assert(_leastRecentlyUsed is not null, "This method shouldn't be called on empty list!");
        if (node.Next == node)
        {
            Debug.Assert(_cacheMap.Count == 1 && _leastRecentlyUsed == node, "this should only be true for a list with only one node");
            _leastRecentlyUsed = null;
        }
        else
        {
            node.Next!.Prev = node.Prev;
            node.Prev!.Next = node.Next;
            if (_leastRecentlyUsed == node)
            {
                _leastRecentlyUsed = node.Next;
            }
        }
    }

    private struct LruCacheItem
    {
        public LruCacheItem(KeccakKey k, byte[] v)
        {
            Key = k;
            Value = v;
        }

        public readonly KeccakKey Key;
        public byte[] Value;

        public long MemorySize => FindMemorySize(Value);

        public static long FindMemorySize(byte[] withValue)
        {
            return MemorySizes.Align(
                Keccak.MemorySize +
                MemorySizes.ArrayOverhead +
                withValue.Length);
        }
    }

    private long CalculateDictionaryPartMemory(int currentCapacity, int newCount)
    {
        int previousSize = _currentDictionaryCapacity * DictionaryItemSize;
        int newSize = previousSize;
        if (newCount > currentCapacity)
        {
            _currentDictionaryCapacity = MemorySizes.FindNextPrime(Math.Max(currentCapacity, 1) * 2);
            newSize = _currentDictionaryCapacity * DictionaryItemSize;
        }

        return newSize - previousSize;
    }

    public long MemorySize { get; private set; } = PreInitMemorySize;
}
