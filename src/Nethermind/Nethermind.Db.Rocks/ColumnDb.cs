// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using RocksDbSharp;

namespace Nethermind.Db.Rocks;

public class ColumnDb : IDbWithSpan
{
    private readonly RocksDb _rocksDb;
    private readonly DbOnTheRocks _mainDb;
    internal readonly ColumnFamilyHandle _columnFamily;

    private DbOnTheRocks.ManagedIterators _readaheadIterators = new();

    public ColumnDb(RocksDb rocksDb, DbOnTheRocks mainDb, string name)
    {
        _rocksDb = rocksDb;
        _mainDb = mainDb;
        _columnFamily = _rocksDb.GetColumnFamily(name);
        Name = name;
    }

    public void Dispose()
    {
        _readaheadIterators.DisposeAll();
    }

    public string Name { get; }

    public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        return _mainDb.GetWithColumnFamily(key, _columnFamily, _readaheadIterators, flags);
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        _mainDb.SetWithColumnFamily(key, _columnFamily, value, flags);
    }

    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] =>
        _rocksDb.MultiGet(keys, keys.Select(k => _columnFamily).ToArray());

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false)
    {
        Iterator iterator = _mainDb.CreateIterator(ordered, _columnFamily);
        return _mainDb.GetAllCore(iterator);
    }

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        Iterator iterator = _mainDb.CreateIterator(ordered, _columnFamily);
        return _mainDb.GetAllValuesCore(iterator);
    }

    public IBatch StartBatch()
    {
        return new ColumnsDbBatch(this, (DbOnTheRocks.RocksDbBatch)_mainDb.StartBatch());
    }

    private class ColumnsDbBatch : IBatch
    {
        private readonly ColumnDb _columnDb;
        private readonly DbOnTheRocks.RocksDbBatch _underlyingBatch;

        public ColumnsDbBatch(ColumnDb columnDb, DbOnTheRocks.RocksDbBatch underlyingBatch)
        {
            _columnDb = columnDb;
            _underlyingBatch = underlyingBatch;
        }

        public void Dispose()
        {
            _underlyingBatch.Dispose();
        }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            return _underlyingBatch.Get(key, flags);
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (value is null)
            {
                _underlyingBatch.Delete(key, _columnDb._columnFamily);
            }
            else
            {
                _underlyingBatch.Set(key, value, _columnDb._columnFamily);
            }
        }
    }

    public void Remove(ReadOnlySpan<byte> key)
    {
        // TODO: this does not participate in batching?
        _rocksDb.Remove(key, _columnFamily, _mainDb.WriteOptions);
    }

    public bool KeyExists(ReadOnlySpan<byte> key) => _rocksDb.Get(key, _columnFamily) is not null;

    public void Flush()
    {
        _mainDb.Flush();
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
    public long GetSize() => _mainDb.GetSize();
    public long GetCacheSize() => _mainDb.GetCacheSize();
    public long GetIndexSize() => _mainDb.GetIndexSize();
    public long GetMemtableSize() => _mainDb.GetMemtableSize();

    public Span<byte> GetSpan(ReadOnlySpan<byte> key) => _rocksDb.GetSpan(key, _columnFamily);

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        _rocksDb.Put(key, value, _columnFamily, _mainDb.WriteOptions);
    }

    public void DangerousReleaseMemory(in Span<byte> span) => _rocksDb.DangerousReleaseMemory(span);

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetIterator()
    {
        using Iterator iterator = _mainDb.CreateIterator(true, _columnFamily);
        return _mainDb.GetAllCore(iterator);
    }

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetIterator(byte[] start)
    {
        using Iterator iterator = _mainDb.CreateIterator(true, _columnFamily);
        iterator.Seek(start);
        return _mainDb.GetAllCore(iterator);
    }

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetIterator(byte[] start, byte[] end)
    {
        using Iterator iterator = _mainDb.CreateIterator(true, _columnFamily);
        iterator.Seek(start);
        return _mainDb.GetAllCore(iterator);
    }
}
