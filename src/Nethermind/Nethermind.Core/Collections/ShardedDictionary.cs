// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Autofac.Extensions.DependencyInjection;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Collections;

// TODO: Maybe remove or redo until it is actually faster
public class ShardedConcurrentDictionary<TKey, TValue>: IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    private Lock[] _locks;
    private Dictionary<TKey, TValue>[] _dictionaries;
    private readonly int _shardCount;

    public ShardedConcurrentDictionary() : this(Environment.ProcessorCount)
    {
    }

    public ShardedConcurrentDictionary(int shardCount)
    {
        if (shardCount <= 0) throw new InvalidOperationException("Shard count must be more than 0");

        _shardCount = shardCount;

        _locks = new Lock[shardCount];
        for (int i = 0; i < shardCount; i++)
        {
            _locks[i] = new Lock();
        }

        _dictionaries = new Dictionary<TKey, TValue>[shardCount];
        for (int i = 0; i < shardCount; i++)
        {
            _dictionaries[i] = new Dictionary<TKey, TValue>();
        }
    }

    public void NoResizeClear()
    {
        for (int i = 0; i < _shardCount; i++)
        {
            using var _ = _locks[i].EnterScope();

            _dictionaries[i].Clear();
        }
    }

    public int Count
    {
        get
        {
            int totalCount = 0;
            for (int i = 0; i < _shardCount; i++)
            {
                using var _ = _locks[i].EnterScope();
                totalCount += _dictionaries[i].Count;
            }
            return totalCount;
        }
    }

    public IEnumerable<TKey> Keys
    {
        get
        {
            // TODO: Need a way to work without this buffer
            using ArrayPoolList<TKey> resultBuffer = new(1);

            for (int i = 0; i < _shardCount; i++)
            {
                using (var _ = _locks[i].EnterScope())
                {
                    resultBuffer.AddRange(_dictionaries[i].Keys);
                }

                foreach (TKey key in resultBuffer)
                {
                    yield return key;
                }

                resultBuffer.Clear();
            }

        }
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        // TODO: Need a way to work without this buffer
        using ArrayPoolList<KeyValuePair<TKey, TValue>> resultBuffer = new ArrayPoolList<KeyValuePair<TKey, TValue>>(1);

        for (int i = 0; i < _shardCount; i++)
        {

            using (var _ = _locks[i].EnterScope())
            {
                resultBuffer.AddRange(_dictionaries[i]);
            }

            foreach (KeyValuePair<TKey, TValue> kv in resultBuffer)
            {
                yield return kv;
            }

            resultBuffer.Clear();
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private int GetShardIdx(TKey key)
    {
        return (key.GetHashCode() & int.MaxValue) % _shardCount;
    }

    public bool TryGetValue(TKey key, out TValue? value)
    {
        int shardIdx = GetShardIdx(key);
        using (var _ = _locks[shardIdx].EnterScope())
        {
            return _dictionaries[shardIdx].TryGetValue(key, out value);
        }
    }

    public TValue this[TKey key]
    {
        get
        {
            int shardIdx = GetShardIdx(key);
            using (var _ = _locks[shardIdx].EnterScope())
            {
                return _dictionaries[shardIdx][key];
            }
        }
        set
        {
            int shardIdx = GetShardIdx(key);
            using (var _ = _locks[shardIdx].EnterScope())
            {
                _dictionaries[shardIdx][key] = value;
            }
        }
    }

    public bool TryRemove(TKey key, out TValue? value)
    {
        int shardIdx = GetShardIdx(key);
        using (var _ = _locks[shardIdx].EnterScope())
        {
            Dictionary<TKey, TValue> dictionary = _dictionaries[shardIdx];
            return dictionary.Remove(key, out value);
        }
    }

    public void Remove(TKey key, out TValue? value)
    {
        int shardIdx = GetShardIdx(key);
        using (var _ = _locks[shardIdx].EnterScope())
        {
            Dictionary<TKey, TValue> dictionary = _dictionaries[shardIdx];
            dictionary.Remove(key, out value);
        }
    }

    public WriteBatch BeginWriteBatch()
    {
        return new WriteBatch(this);
    }

    public class WriteBatch : IDisposable
    {
        private int MaxBufferSize = 64;
        private ArrayPoolList<(TKey, TValue)>[] _buffers;
        private readonly ShardedConcurrentDictionary<TKey, TValue> _dictionary;
        private Lock[] _locks;

        public WriteBatch(ShardedConcurrentDictionary<TKey, TValue> dictionary)
        {
            _dictionary = dictionary;
            _buffers = new ArrayPoolList<(TKey, TValue)>[dictionary._shardCount];
            for (int i = 0; i < _dictionary._shardCount; i++)
            {
                _buffers[i] = new ArrayPoolList<(TKey, TValue)>(MaxBufferSize);
            }

            // Separate lock from the one in dictionary by the way
            _locks = new Lock[dictionary._shardCount];
            for (int i = 0; i < dictionary._shardCount; i++)
            {
                _locks[i] = new Lock();
            }
        }

        public void Set(in TKey key, in TValue value)
        {
            if (_wasDisposed) throw new InvalidOperationException("Write batch was disposed");

            int shardId = _dictionary.GetShardIdx(key);

            using var _ =  _locks[shardId].EnterScope();
            var buffer = _buffers[shardId];
            buffer.Add((key, value));

            if (buffer.Count >= MaxBufferSize)
            {
                using var lock2 = _dictionary._locks[shardId].EnterScope();
                Dictionary<TKey, TValue> plainDictionary = _dictionary._dictionaries[shardId];

                foreach (var kv in buffer)
                {
                    plainDictionary[kv.Item1] = kv.Item2;
                }

                buffer.Clear();
            }
        }

        private bool _wasDisposed = false;

        public void Dispose()
        {
            if (_wasDisposed) return;
            _wasDisposed = true;

            for (int i = 0; i < _buffers.Length; i++)
            {
                int shardId = i;
                using var lock2 = _dictionary._locks[shardId].EnterScope();
                var buffer = _buffers[shardId];

                Dictionary<TKey, TValue> plainDictionary = _dictionary._dictionaries[shardId];
                foreach (var kv in buffer)
                {
                    plainDictionary[kv.Item1] = kv.Item2;
                }

                buffer.Dispose();
            }
        }
    }
}
