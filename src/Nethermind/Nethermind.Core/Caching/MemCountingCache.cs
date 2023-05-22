// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Caching
{
    [DebuggerDisplay("MemCountingCache (Single: {SingleAccessCount}, Multi: {MultiAccessCount})")]
    public sealed class MemCountingCache : ICache<ValueKeccak, byte[]>
    {
        private readonly int _maxCapacity;
        private readonly Dictionary<ValueKeccak, LinkedListNode<LruCacheItem>> _cacheMap;
        private LinkedListNode<LruCacheItem>? _singleAccessLru;
        private LinkedListNode<LruCacheItem>? _multiAccessLru;

        public int SingleAccessCount { get; private set; }
        public int MultiAccessCount { get; private set; }

        private const int PreInitMemorySize =
            80 /* Dictionary */ +
            MemorySizes.SmallObjectOverhead +
            MemorySizes.SmallObjectOverhead +
            MemorySizes.SmallObjectOverhead +
            8 /* sizeof(int) * 2 */ +
            8 /* sizeof(int) aligned */;

        private const int PostInitMemorySize =
            52 /* lazy loaded dictionary.Items */ + PreInitMemorySize;

        private const int DictionaryItemSize = 28;
        private int _currentDictionaryCapacity;

        public void Clear()
        {
            _singleAccessLru = null;
            _multiAccessLru = null;
            SingleAccessCount = 0;
            MultiAccessCount = 0;
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

        public byte[]? Get(ValueKeccak key)
        {
            if (TryGet(key, out byte[]? value))
            {
                return value;
            }

            return default;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool TryGet(ValueKeccak key, out byte[]? value)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                value = node.Value.Value;
                ulong accessCount = node.AccessCount;
                LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _singleAccessLru, ref _multiAccessLru, node);
                if (accessCount == 1)
                {
                    SingleAccessCount--;
                    MultiAccessCount++;
                }

                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Set(ValueKeccak key, byte[]? val)
        {
            if (val is null)
            {
                return Delete(key);
            }

            if (_cacheMap.TryGetValue(key, out LinkedListNode<LruCacheItem>? node))
            {
                node.Value.Value = val;
                ulong accessCount = node.AccessCount;
                LinkedListNode<LruCacheItem>.MoveToMostRecent(ref _singleAccessLru, ref _multiAccessLru, node);
                if (accessCount == 1)
                {
                    SingleAccessCount--;
                    MultiAccessCount++;
                }

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
                    SingleAccessCount++;
                    LinkedListNode<LruCacheItem> newNode = new(new(key, val));
                    LinkedListNode<LruCacheItem>.AddMostRecent(ref _singleAccessLru, newNode);
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
        public bool Delete(ValueKeccak key)
        {
            if (_cacheMap.Remove(key, out LinkedListNode<LruCacheItem>? node))
            {
                if (node.AccessCount == 1)
                {
                    SingleAccessCount--;
                    LinkedListNode<LruCacheItem>.Remove(ref _singleAccessLru, node);
                }
                else
                {
                    MultiAccessCount--;
                    LinkedListNode<LruCacheItem>.Remove(ref _multiAccessLru, node);
                }

                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool Contains(ValueKeccak key) => _cacheMap.ContainsKey(key);

        private void Replace(ValueKeccak key, byte[] value)
        {
            LinkedListNode<LruCacheItem>? node;
            if (_singleAccessLru is null ||
                (MultiAccessCount > _maxCapacity / 2
                // Only if last access was earlier than the oldest single access item
                 && _multiAccessLru!.LastAccessSec < _singleAccessLru.LastAccessSec))
            {
                MultiAccessCount--;
                SingleAccessCount++;
                node = _multiAccessLru;
                Debug.Assert(node is not null && node.AccessCount > 1);
                LinkedListNode<LruCacheItem>.Remove(ref _multiAccessLru, node);
            }
            else
            {
                node = _singleAccessLru;
                Debug.Assert(node is not null && node.AccessCount == 1);
                LinkedListNode<LruCacheItem>.Remove(ref _singleAccessLru, node);
            }

            if (node is null)
            {
                ThrowInvalidOperationException();
            }

            MemorySize += MemorySizes.Align(value.Length) - MemorySizes.Align(node.Value.Value.Length);
            if (!_cacheMap.Remove(node.Value.Key))
            {
                ThrowInvalidOperationException();
            }

            node.Value = new(key, value);
            node.ResetAccessCount();

            LinkedListNode<LruCacheItem>.AddMostRecent(ref _singleAccessLru, node);
            _cacheMap.Add(key, node);
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException(
                $"{nameof(MemCountingCache)} called {nameof(Replace)} when empty.");
        }

        [DebuggerDisplay("{Key}:{Value}")]
        private struct LruCacheItem
        {
            public LruCacheItem(ValueKeccak k, byte[] v)
            {
                Key = k;
                Value = v;
            }

            public readonly ValueKeccak Key;
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
}
