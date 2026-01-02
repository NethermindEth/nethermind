// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using RocksDbSharp;

namespace Nethermind.Db.Rocks;

/// <summary>
/// Used by `DbOnTheRocks`, `ColumnDb` and `RocksDbSnapshot` to ensure all the method of
/// `ISortedKeyValueStore` is implemented for the three class. The three class is expected
/// to create their relevent read options and create this class then call this class instead of
/// implementing `ISortedKeyValueStore` implementation themselves.
/// This tend to call `DbOnTheRocks` back though.
/// </summary>
public class RocksDbReader : ISortedKeyValueStore
{
    private readonly DbOnTheRocks _mainDb;
    private readonly Func<ReadOptions> _readOptionsFactory;
    private readonly DbOnTheRocks.IteratorManager? _iteratorManager;
    private readonly ColumnFamilyHandle? _columnFamily;

    readonly ReadOptions _options;
    readonly ReadOptions _hintCacheMissOptions;

    public RocksDbReader(DbOnTheRocks mainDb,
        Func<ReadOptions> readOptionsFactory,
        DbOnTheRocks.IteratorManager? iteratorManager,
        ColumnFamilyHandle? columnFamily)
    {
        _mainDb = mainDb;
        _readOptionsFactory = readOptionsFactory;
        _iteratorManager = iteratorManager;
        _columnFamily = columnFamily;

        _options = readOptionsFactory();
        _hintCacheMissOptions = readOptionsFactory();
        _hintCacheMissOptions.SetFillCache(false);
    }

    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        if ((flags & ReadFlags.HintReadAhead) != 0 && _iteratorManager is not null)
        {
            byte[]? result = _mainDb.GetWithIterator(key, _columnFamily, _iteratorManager, flags, out bool success);
            if (success)
            {
                return result;
            }
        }

        ReadOptions readOptions = ((flags & ReadFlags.HintCacheMiss) != 0 ? _hintCacheMissOptions : _options);
        return _mainDb.Get(key, _columnFamily, readOptions);
    }

    public int Get(scoped ReadOnlySpan<byte> key, Span<byte> output, ReadFlags flags = ReadFlags.None)
    {
        ReadOptions readOptions = ((flags & ReadFlags.HintCacheMiss) != 0 ? _hintCacheMissOptions : _options);
        return _mainDb.GetCStyleWithColumnFamily(key, output, _columnFamily, readOptions);
    }

    public Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        ReadOptions readOptions = ((flags & ReadFlags.HintCacheMiss) != 0 ? _hintCacheMissOptions : _options);
        return _mainDb.GetSpanWithColumnFamily(key, _columnFamily, readOptions);
    }

    public void DangerousReleaseMemory(in ReadOnlySpan<byte> span)
    {
        _mainDb.DangerousReleaseMemory(span);
    }

    public bool KeyExists(ReadOnlySpan<byte> key)
    {
        return _mainDb.KeyExistsWithColumn(key, _columnFamily);
    }


    public byte[]? FirstKey
    {
        get
        {
            using Iterator iterator = _mainDb.CreateIterator(_options, _columnFamily);
            iterator.SeekToFirst();
            return iterator.Valid() ? iterator.GetKeySpan().ToArray() : null;
        }
    }

    public byte[]? LastKey
    {
        get
        {
            using Iterator iterator = _mainDb.CreateIterator(_options, _columnFamily);
            iterator.SeekToLast();
            return iterator.Valid() ? iterator.GetKeySpan().ToArray() : null;
        }
    }

    public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKey, ReadOnlySpan<byte> lastKey)
    {
        ReadOptions readOptions = _readOptionsFactory();

        unsafe
        {
            IntPtr iterateLowerBound = Marshal.AllocHGlobal(firstKey.Length);
            firstKey.CopyTo(new Span<byte>(iterateLowerBound.ToPointer(), firstKey.Length));
            Native.Instance.rocksdb_readoptions_set_iterate_lower_bound(readOptions.Handle, iterateLowerBound, (UIntPtr)firstKey.Length);

            IntPtr iterateUpperBound = Marshal.AllocHGlobal(lastKey.Length);
            lastKey.CopyTo(new Span<byte>(iterateUpperBound.ToPointer(), lastKey.Length));
            Native.Instance.rocksdb_readoptions_set_iterate_upper_bound(readOptions.Handle, iterateUpperBound, (UIntPtr)lastKey.Length);
        }

        Iterator iterator = _mainDb.CreateIterator(readOptions, _columnFamily);
        return new RocksdbSortedView(iterator);
    }
}
