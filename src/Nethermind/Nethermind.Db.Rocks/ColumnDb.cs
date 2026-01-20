// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        _reader = new RocksDbReader(mainDb, () =>
        {
            // TODO: Verify checksum not set here.
            return new ReadOptions();
        }, _iteratorManager, _columnFamily);
    }

    public void Dispose()
    {
        _iteratorManager.Dispose();
    }

    public string Name { get; }

    byte[]? IReadOnlyKeyValueStore.Get(ReadOnlySpan<byte> key, ReadFlags flags)
    {
        return _reader.Get(key, flags);
    }

    Span<byte> IReadOnlyKeyValueStore.GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags)
    {
        return _reader.GetSpan(key, flags);
    }

    int IReadOnlyKeyValueStore.Get(scoped ReadOnlySpan<byte> key, Span<byte> output, ReadFlags flags)
    {
        return _reader.Get(key, output, flags);
    }

    bool IReadOnlyKeyValueStore.KeyExists(ReadOnlySpan<byte> key)
    {
        return _reader.KeyExists(key);
    }

    void IReadOnlyKeyValueStore.DangerousReleaseMemory(in ReadOnlySpan<byte> key)
    {
        _reader.DangerousReleaseMemory(key);
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        _mainDb.SetWithColumnFamily(key, _columnFamily, value, flags);
    }

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags writeFlags = WriteFlags.None)
    {
        _mainDb.SetWithColumnFamily(key, _columnFamily, value, writeFlags);
    }

    public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags writeFlags = WriteFlags.None)
    {
        _mainDb.MergeWithColumnFamily(key, _columnFamily, value, writeFlags);
    }

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

    public IWriteBatch StartWriteBatch()
    {
        return new ColumnsDbWriteBatch(this, (DbOnTheRocks.RocksDbWriteBatch)_mainDb.StartWriteBatch());
    }

    private class ColumnsDbWriteBatch : IWriteBatch
    {
        private readonly ColumnDb _columnDb;
        private readonly DbOnTheRocks.RocksDbWriteBatch _underlyingWriteBatch;

        public ColumnsDbWriteBatch(ColumnDb columnDb, DbOnTheRocks.RocksDbWriteBatch underlyingWriteBatch)
        {
            _columnDb = columnDb;
            _underlyingWriteBatch = underlyingWriteBatch;
        }

        public void Dispose()
        {
            _underlyingWriteBatch.Dispose();
        }

        public void Clear()
        {
            _underlyingWriteBatch.Clear();
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (value is null)
            {
                _underlyingWriteBatch.Delete(key, _columnDb._columnFamily);
            }
            else
            {
                _underlyingWriteBatch.Set(key, value, _columnDb._columnFamily, flags);
            }
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            _underlyingWriteBatch.Set(key, value, _columnDb._columnFamily, flags);
        }

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            _underlyingWriteBatch.Merge(key, value, _columnDb._columnFamily, flags);
        }
    }

    public void Remove(ReadOnlySpan<byte> key)
    {
        Set(key, null);
    }

    public void Flush(bool onlyWal)
    {
        _mainDb.FlushWithColumnFamily(_columnFamily);
    }

    public void Compact()
    {
        _rocksDb.CompactRange(Keccak.Zero.BytesToArray(), Keccak.MaxValue.BytesToArray(), _columnFamily);
    }

    /// <summary>
    /// Not sure how to handle delete of the columns DB
    /// </summary>
    /// <exception cref="NotSupportedException"></exception>
    public void Clear() { throw new NotSupportedException(); }

    // Maybe it should be column specific metric?
    public IDbMeta.DbMetric GatherMetric() => _mainDb.GatherMetric();

    public byte[]? FirstKey
    {
        get
        {
            ReadOptions readOptions = new();
            using Iterator iterator = _mainDb.CreateIterator(readOptions, ch: _columnFamily);
            iterator.SeekToFirst();
            return iterator.Valid() ? iterator.GetKeySpan().ToArray() : null;
        }
    }

    public byte[]? LastKey
    {
        get
        {
            ReadOptions readOptions = new();
            using Iterator iterator = _mainDb.CreateIterator(readOptions, ch: _columnFamily);
            iterator.SeekToLast();
            return iterator.Valid() ? iterator.GetKeySpan().ToArray() : null;
        }
    }

    public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKey, ReadOnlySpan<byte> lastKey)
    {
        return _mainDb.GetViewBetween(firstKey, lastKey, _columnFamily);
    }

    public IKeyValueStoreSnapshot CreateSnapshot()
    {
        Snapshot snapshot = _rocksDb.CreateSnapshot();

        return new DbOnTheRocks.RocksDbSnapshot(
            _mainDb,
            () =>
            {
                ReadOptions readOptions = new();
                readOptions.SetSnapshot(snapshot);
                return readOptions;
            },
            _columnFamily,
            snapshot);
    }
}
