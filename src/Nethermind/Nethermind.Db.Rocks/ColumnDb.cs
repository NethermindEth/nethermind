// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using RocksDbSharp;
using IWriteBatch = Nethermind.Core.IWriteBatch;

namespace Nethermind.Db.Rocks;

public class ColumnDb : IDb, ISortedKeyValueStore, IMergeableKeyValueStore, IKeyValueStoreWithSnapshot
{
    private readonly RocksDb _rocksDb;
    internal readonly DbOnTheRocks _mainDb;
    internal readonly ColumnFamilyHandle _columnFamily;

    private readonly DbOnTheRocks.IteratorManager _iteratorManager;
    private readonly RocksDbReader _reader;

    public ColumnDb(RocksDb rocksDb, DbOnTheRocks mainDb, string name)
    {
        _rocksDb = rocksDb;
        _mainDb = mainDb;
        if (name == "Default") name = "default";
        _columnFamily = _rocksDb.GetColumnFamily(name);
        Name = name;

        _iteratorManager = new DbOnTheRocks.IteratorManager(_rocksDb, _columnFamily, _mainDb._readAheadReadOptions);
        _reader = new RocksDbReader(mainDb, mainDb.CreateReadOptions, _iteratorManager, _columnFamily);
    }

    public void Dispose() => _iteratorManager.Dispose();
    public string Name { get; }

    byte[]? IReadOnlyKeyValueStore.Get(ReadOnlySpan<byte> key, ReadFlags flags) => _reader.Get(key, flags);

    Span<byte> IReadOnlyKeyValueStore.GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags) => _reader.GetSpan(key, flags);

    MemoryManager<byte>? IReadOnlyKeyValueStore.GetOwnedMemory(ReadOnlySpan<byte> key, ReadFlags flags)
    {
        Span<byte> span = ((IReadOnlyKeyValueStore)this).GetSpan(key, flags);
        return span.IsNullOrEmpty() ? null : new DbSpanMemoryManager(this, span);
    }


    int IReadOnlyKeyValueStore.Get(scoped ReadOnlySpan<byte> key, Span<byte> output, ReadFlags flags) => _reader.Get(key, output, flags);

    bool IReadOnlyKeyValueStore.KeyExists(ReadOnlySpan<byte> key) => _reader.KeyExists(key);

    void IReadOnlyKeyValueStore.DangerousReleaseMemory(in ReadOnlySpan<byte> key) => _reader.DangerousReleaseMemory(key);

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) =>
        _mainDb.SetWithColumnFamily(key, _columnFamily, value, flags);

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags writeFlags = WriteFlags.None) =>
        _mainDb.SetWithColumnFamily(key, _columnFamily, value, writeFlags);

    public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags writeFlags = WriteFlags.None) =>
        _mainDb.MergeWithColumnFamily(key, _columnFamily, value, writeFlags);

    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys]
    {
        get
        {
            ColumnFamilyHandle[] columnFamilies = new ColumnFamilyHandle[keys.Length];
            Array.Fill(columnFamilies, _columnFamily);
            return _rocksDb.MultiGet(keys, columnFamilies);
        }
    }

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false)
    {
        Iterator iterator = _mainDb.CreateIterator(ordered, _columnFamily);
        return _mainDb.GetAllCore(iterator);
    }

    public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
    {
        Iterator iterator = _mainDb.CreateIterator(ordered, _columnFamily);
        return _mainDb.GetAllKeysCore(iterator);
    }

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        Iterator iterator = _mainDb.CreateIterator(ordered, _columnFamily);
        return _mainDb.GetAllValuesCore(iterator);
    }

    public IWriteBatch StartWriteBatch() => new ColumnsDbWriteBatch(this, (DbOnTheRocks.RocksDbWriteBatch)_mainDb.StartWriteBatch());

    private class ColumnsDbWriteBatch(ColumnDb columnDb, DbOnTheRocks.RocksDbWriteBatch underlyingWriteBatch)
        : IWriteBatch
    {
        public void Dispose() => underlyingWriteBatch.Dispose();

        public void Clear() => underlyingWriteBatch.Clear();

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (value is null)
            {
                underlyingWriteBatch.Delete(key, columnDb._columnFamily);
            }
            else
            {
                underlyingWriteBatch.Set(key, value, columnDb._columnFamily, flags);
            }
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
            underlyingWriteBatch.Set(key, value, columnDb._columnFamily, flags);

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
            underlyingWriteBatch.Merge(key, value, columnDb._columnFamily, flags);
    }

    public void Remove(ReadOnlySpan<byte> key) => Set(key, null);

    public void Flush(bool onlyWal) => _mainDb.FlushWithColumnFamily(_columnFamily);

    public void Compact() =>
        _rocksDb.CompactRange(Keccak.Zero.BytesToArray(), Keccak.MaxValue.BytesToArray(), _columnFamily);

    /// <summary>
    /// Not sure how to handle delete of the columns DB
    /// </summary>
    /// <exception cref="NotSupportedException"></exception>
    public void Clear() => throw new NotSupportedException();

    // Maybe it should be column-specific metric?
    public IDbMeta.DbMetric GatherMetric() => _mainDb.GatherMetric();

    public byte[]? FirstKey
    {
        get
        {
            using Iterator iterator = _mainDb.CreateIterator(_mainDb.CreateReadOptions(), ch: _columnFamily);
            iterator.SeekToFirst();
            return iterator.Valid() ? iterator.GetKeySpan().ToArray() : null;
        }
    }

    public byte[]? LastKey
    {
        get
        {
            using Iterator iterator = _mainDb.CreateIterator(_mainDb.CreateReadOptions(), ch: _columnFamily);
            iterator.SeekToLast();
            return iterator.Valid() ? iterator.GetKeySpan().ToArray() : null;
        }
    }

    public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKey, ReadOnlySpan<byte> lastKey) =>
        _mainDb.GetViewBetween(firstKey, lastKey, _columnFamily);

    public IKeyValueStoreSnapshot CreateSnapshot()
    {
        Snapshot snapshot = _rocksDb.CreateSnapshot();

        return new DbOnTheRocks.RocksDbSnapshot(
            _mainDb,
            () =>
            {
                ReadOptions readOptions = _mainDb.CreateReadOptions();
                readOptions.SetSnapshot(snapshot);
                return readOptions;
            },
            _columnFamily,
            snapshot);
    }
}
