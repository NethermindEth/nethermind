// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using RocksDbSharp;
using IWriteBatch = Nethermind.Core.IWriteBatch;

namespace Nethermind.Db.Rocks;

public class ColumnDb : IDb
{
    private readonly RocksDb _rocksDb;
    internal readonly DbOnTheRocks _mainDb;
    internal readonly ColumnFamilyHandle _columnFamily;

    private readonly DbOnTheRocks.ManagedIterators _readaheadIterators = new();

    public ColumnDb(RocksDb rocksDb, DbOnTheRocks mainDb, string name)
    {
        _rocksDb = rocksDb;
        _mainDb = mainDb;
        if (name == "Default") name = "default";
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

    public Span<byte> GetSpan(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        return _mainDb.GetSpanWithColumnFamily(key, _columnFamily);
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        _mainDb.SetWithColumnFamily(key, _columnFamily, value, flags);
    }

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags writeFlags = WriteFlags.None)
    {
        _mainDb.SetWithColumnFamily(key, _columnFamily, value, writeFlags);
    }

    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] =>
        _rocksDb.MultiGet(keys, keys.Select(k => _columnFamily).ToArray());

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

    public void DangerousReleaseMemory(in Span<byte> span)
    {
        _mainDb.DangerousReleaseMemory(span);
    }
}
