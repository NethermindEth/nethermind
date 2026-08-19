// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.RocksDbBindings;

namespace Nethermind.Db.Rocks;

/// <summary>
/// Used by `DbOnTheRocks`, `ColumnDb` and `RocksDbSnapshot` to ensure all the methods of
/// `ISortedKeyValueStore` are implemented for the three classes. The three classes are expected
/// to create their relevant read options and create this class then call this class instead of
/// implementing `ISortedKeyValueStore` implementation themselves.
/// This tends to call `DbOnTheRocks` back though.
/// </summary>
/// <remarks>
/// Constructor that accepts pre-created <see cref="ReadOptions"/> instead of a factory.
/// Used by <see cref="ColumnsDb{T}.ColumnDbSnapshot"/> to share a single pair of ReadOptions
/// across all column readers, avoiding per-reader native handle allocation and finalizer pressure.
/// </remarks>
public class RocksDbReader(DbOnTheRocks mainDb,
    ReadOptions options,
    ReadOptions hintCacheMissOptions,
    Func<ReadOptions> readOptionsFactory,
    DisposableLazy<DbOnTheRocks.IteratorManager>? iteratorManager = null,
    IColumnFamilyHandle? columnFamily = null) : ISortedKeyValueStore, IDisposable
{
    private readonly DbOnTheRocks _mainDb = mainDb;
    private readonly Func<ReadOptions> _readOptionsFactory = readOptionsFactory;
    private readonly DisposableLazy<DbOnTheRocks.IteratorManager>? _iteratorManager = iteratorManager;
    private readonly IColumnFamilyHandle? _columnFamily = columnFamily;

    private readonly ReadOptions _options = options;
    private readonly ReadOptions _hintCacheMissOptions = hintCacheMissOptions;
    private readonly bool _ownsReadOptions;
    private int _disposed;

    public RocksDbReader(DbOnTheRocks mainDb,
        Func<ReadOptions> readOptionsFactory,
        DisposableLazy<DbOnTheRocks.IteratorManager>? iteratorManager = null,
        IColumnFamilyHandle? columnFamily = null)
        : this(mainDb, readOptionsFactory(), readOptionsFactory(), readOptionsFactory, iteratorManager, columnFamily)
    {
        _ownsReadOptions = true;
        _hintCacheMissOptions.SetFillCache(false);
    }

    public virtual void Dispose()
    {
        if (!_ownsReadOptions || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _options.Dispose();
        _hintCacheMissOptions.Dispose();
    }

    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        if ((flags & ReadFlags.HintReadAhead) != 0 && _iteratorManager is not null)
        {
            byte[]? result = _mainDb.GetWithIterator(key, _iteratorManager.Value, flags, out bool success);
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

    public void DangerousReleaseMemory(in ReadOnlySpan<byte> span) => _mainDb.DangerousReleaseMemory(span);

    public bool KeyExists(ReadOnlySpan<byte> key) => _mainDb.KeyExistsWithColumn(key, _columnFamily);


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
        readOptions.SetIterateBounds(firstKey, lastKey);

        Iterator iterator = _mainDb.CreateIterator(readOptions, _columnFamily);
        return new RocksdbSortedView(iterator, readOptions);
    }
}
