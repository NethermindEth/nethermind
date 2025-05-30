// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ConcurrentCollections;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Db;

public class AsyncDb(IDb db) : IDb
{
    private readonly ConcurrentDictionary<Dictionary<byte[], byte[]?>, Dictionary<byte[], byte[]?>.AlternateLookup<ReadOnlySpan<byte>>> _asyncBatches = new();
    private readonly ConcurrentHashSet<Task> _writeTasks = new();
    private int _asyncBatchesCount = 0;
    private readonly IDb _db = db;

    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        if (Volatile.Read(ref _asyncBatchesCount) > 0)
        {
            foreach (var kv in _asyncBatches)
            {
                if (kv.Value.TryGetValue(key, out var value))
                {
                    return value;
                }
            }
        }

        return _db.Get(key, flags);
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        _db.Set(key, value, flags);
    }

    public IWriteBatch StartWriteBatch() => new AsyncBatch(this);

    public void Flush(bool onlyWal = false)
    {
        Task.WaitAll(_writeTasks);
        _db.Flush(onlyWal);
    }

    public void Dispose()
    {
        Task.WaitAll(_writeTasks);
        _db.Dispose();
    }

    public string Name => _db.Name;

    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys]
    {
        get
        {
            if (Volatile.Read(ref _asyncBatchesCount) > 0)
            {
                Dictionary<byte[], byte[]?> result = new(keys.Length);

                foreach (byte[] key in keys)
                {
                    foreach (var kv in _asyncBatches)
                    {
                        if (kv.Key.TryGetValue(key, out var value))
                        {
                            result.Add(key, value);
                        }
                    }
                }

                foreach (KeyValuePair<byte[], byte[]> values in _db[keys])
                {
                    ref byte[] value = ref CollectionsMarshal.GetValueRefOrAddDefault(result, values.Key, out bool exists);
                    if (!exists)
                    {
                        value = values.Value;
                    }
                }

                return result.ToArray();
            }

            return _db[keys];
        }
    }

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false)
    {
        // TODO: ordered?
        if (Volatile.Read(ref _asyncBatchesCount) > 0)
        {
            HashSet<byte[]> keys = new();
            foreach (var kv in _asyncBatches)
            {
                foreach (KeyValuePair<byte[], byte[]> value in kv.Key)
                {
                    keys.Add(value.Key);
                    yield return value;
                }
            }

            foreach (KeyValuePair<byte[], byte[]> value in _db.GetAll(ordered))
            {
                if (!keys.Contains(value.Key))
                {
                    yield return value;
                }
            }
        }
        else
        {
            foreach (KeyValuePair<byte[], byte[]> value in _db.GetAll(ordered))
            {
                yield return value;
            }
        }
    }

    public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
    {
        if (Volatile.Read(ref _asyncBatchesCount) > 0)
        {
            foreach (KeyValuePair<byte[], byte[]> kv in GetAll(ordered))
            {
                yield return kv.Key;
            }
        }
        else
        {
            foreach (byte[] key in _db.GetAllKeys(ordered))
            {
                yield return key;
            }
        }
    }

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        if (Volatile.Read(ref _asyncBatchesCount) > 0)
        {
            foreach (KeyValuePair<byte[], byte[]> kv in GetAll(ordered))
            {
                yield return kv.Value;
            }
        }
        else
        {
            foreach (byte[] value in _db.GetAllValues(ordered))
            {
                yield return value;
            }
        }
    }

    private class AsyncBatch(AsyncDb asyncDb) : IWriteBatch
    {
        private readonly Dictionary<byte[], byte[]?> _dictionary = new(Bytes.EqualityComparer);
        private WriteFlags _flags = WriteFlags.None;

        public void Dispose()
        {
            Interlocked.Increment(ref asyncDb._asyncBatchesCount);
            asyncDb._asyncBatches.TryAdd(_dictionary, _dictionary.GetAlternateLookup<ReadOnlySpan<byte>>());
            Task task = Task.Run(() =>
            {
                using IWriteBatch batch = asyncDb._db.StartWriteBatch();
                WriteFlags flags = _flags;
                foreach (var kv in _dictionary)
                {
                    batch.Set(kv.Key, kv.Value, flags);
                }

                asyncDb._asyncBatches.TryRemove(_dictionary, out _);
                Interlocked.Decrement(ref asyncDb._asyncBatchesCount);
            });

            task.ContinueWith(_ => asyncDb._writeTasks.TryRemove(task));
            asyncDb._writeTasks.Add(task);
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _dictionary[key.ToArray()] = value;
            _flags = flags;
        }

        public bool PreferWriteByArray => true;
    }
}
