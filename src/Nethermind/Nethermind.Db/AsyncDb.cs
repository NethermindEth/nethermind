// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db;

public class AsyncDb : IAsyncDb
{
    private readonly ConcurrentDictionary<Dictionary<byte[], byte[]?>, Dictionary<byte[], byte[]?>.AlternateLookup<ReadOnlySpan<byte>>> _asyncBatches = new();
    private readonly Channel<Dictionary<byte[], byte[]?>> _channel = Channel.CreateBounded<Dictionary<byte[], byte[]?>>(new BoundedChannelOptions(1024) { SingleReader = true });
    private readonly Task _asyncTask;
    private int _asyncBatchesCount = 0;
    private readonly IDb _db;
    private readonly ILogger _logger;

    public AsyncDb(IDb db, ILogger logger)
    {
        _db = db;
        _logger = logger;
        _asyncTask = WriteAsync(_channel);
    }

    private async Task WriteAsync(Channel<Dictionary<byte[], byte[]>> channel)
    {
        await foreach (Dictionary<byte[], byte[]> data in channel.Reader.ReadAllAsync())
        {
            try
            {
                if (_logger.IsInfo) _logger.Info($"Start saving batch with count {data.Count}, channel count {channel.Reader.Count}, batch count {_asyncBatchesCount}");
                SaveBatch(data);

                _asyncBatches.TryRemove(data, out _);
                Interlocked.Decrement(ref _asyncBatchesCount);
                if (_logger.IsInfo) _logger.Info($"Saved batch with count {data.Count}, channel count {channel.Reader.Count}, batch count {_asyncBatchesCount}");
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Failed to save batch with count {data.Count}, channel count {channel.Reader.Count}, batch count {_asyncBatchesCount}", e);
                if (!channel.Writer.TryWrite(data))
                {
                    if (_logger.IsError) _logger.Error($"Failed to re-add batch with count {data.Count}, channel count {channel.Reader.Count}, batch count {_asyncBatchesCount}");
                }
            }
        }
    }

    private void SaveBatch(Dictionary<byte[], byte[]> data)
    {
        using IWriteBatch batch = _db.StartWriteBatch();
        // Insert ordered for improved performance
        foreach (KeyValuePair<byte[], byte[]> kv in data.OrderBy(static kvp => kvp.Key, Bytes.Comparer))
        {
            batch.Set(kv.Key, kv.Value);
        }
    }

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

    public IWriteBatch StartWriteBatch() => _db.StartWriteBatch();

    public IWriteBatch StartAsyncWriteBatch() => new AsyncBatch(this);

    public void Flush(bool onlyWal = false)
    {
        _db.Flush(onlyWal);
    }

    public void Dispose()
    {
        _channel.Writer.Complete();
        Parallel.ForEach(_asyncBatches, batch => SaveBatch(batch.Key));
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

        public void Dispose()
        {
            Interlocked.Increment(ref asyncDb._asyncBatchesCount);
            asyncDb._asyncBatches.TryAdd(_dictionary, _dictionary.GetAlternateLookup<ReadOnlySpan<byte>>());
            if (!asyncDb._channel.Writer.TryWrite(_dictionary))
            {
                if (asyncDb._logger.IsError) asyncDb._logger.Error($"Failed to add batch with count {_dictionary.Count}, channel count {asyncDb._channel.Reader.Count}, batch count {asyncDb._asyncBatchesCount}");
            }
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _dictionary[key.ToArray()] = value;
        }

        public bool PreferWriteByArray => true;
    }
}
