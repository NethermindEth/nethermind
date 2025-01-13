// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Db;

public interface IFilePagesPool : IAsyncDisposable
{
    int PageSize { get; }
    SafeFileHandle FileHandle { get; }

    PagesStats Stats { get; }

    ValueTask StartAsync();
    ValueTask StopAsync();

    ValueTask<long> TakePageAsync();
    ValueTask ReturnPageAsync(long offset);
}

public sealed class AsyncFilePagesPool : IFilePagesPool
{
    private const int MaxAllocateAtOnceSize = 10 * 1024 * 1024; // 10MB

    private static readonly byte[] StoreKey = "FreePages"u8.ToArray();
    private readonly string _filePath;
    private readonly IKeyValueStore _store;

    private readonly CancellationTokenSource _cancellationSource = new();
    private CancellationToken CancellationToken => _cancellationSource.Token;

    private readonly Lazy<int> _lazyStart;

    private SafeFileHandle? _fileHandle;
    private FileStream? _fileStream;
    private Task? _allocatePagesTask;

    private Channel<long>? _allocatedPool;
    private Channel<long>? _returnedPool;

    private long _pagesAllocated;
    private long _pagesTaken;
    private long _pagesReturned;
    private long _pagesReused;

    // Stats values are fetched in non-atomic way
    public PagesStats Stats => new()
    {
        PagesAllocated = _pagesAllocated,
        PagesTaken = _pagesTaken,
        PagesReturned = _pagesReturned,
        PagesReused = _pagesReused,
        AllocatedPagesPending = _allocatedPool?.Reader.Count ?? 0,
        ReturnedPagesPending = _returnedPool?.Reader.Count ?? 0
    };

    public int PageSize { get; }

    public SafeFileHandle FileHandle => _fileHandle ?? throw ThrowNotStarted();

    /// <summary>
    /// Size of the pool of pre-allocated free pages.
    /// </summary>
    public int AllocatedPagesPoolSize { get; init; } = 1024;

    /// <summary>
    /// Size of the pool of returned pages.
    /// </summary>
    public int ReturnedPagesPoolSize { get; init; } = 1024;

    public AsyncFilePagesPool(string filePath, IKeyValueStore store, int pageSize)
    {
        _filePath = filePath;
        _store = store;
        _lazyStart = new(StartOnce);

        PageSize = pageSize;
    }

    public ValueTask StartAsync()
    {
        _ = _lazyStart.Value;
        return ValueTask.CompletedTask;
    }

    // TODO: make thread-safe
    public async ValueTask StopAsync()
    {
        if (_allocatePagesTask == null || _allocatedPool == null || _returnedPool == null)
            return;

        try
        {
            if (_cancellationSource.IsCancellationRequested)
                return;

            await _cancellationSource.CancelAsync();
            await _allocatePagesTask.IgnoreException();

            _allocatedPool.Writer.Complete();
            _returnedPool.Writer.Complete();

            StorePooledPages();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }
    }

    public async ValueTask<long> TakePageAsync()
    {
        if (_allocatedPool == null)
            ThrowNotStarted();

        var page = await _allocatedPool.Reader.ReadAsync(CancellationToken);
        Interlocked.Increment(ref _pagesTaken);
        return page;
    }

    public ValueTask ReturnPageAsync(long offset)
    {
        if (_returnedPool == null)
            ThrowNotStarted();

        Interlocked.Increment(ref _pagesReturned);
        return _returnedPool.Writer.WriteAsync(offset, CancellationToken);
    }

    private static Channel<T> CreateChannel<T>(int size) => size <= 0
        ? Channel.CreateUnbounded<T>()
        : Channel.CreateBounded<T>(size);

    private int StartOnce()
    {
        _fileHandle = File.OpenHandle(_filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        _fileStream = new(_fileHandle, FileAccess.ReadWrite);

        _allocatedPool = CreateChannel<long>(AllocatedPagesPoolSize);
        _returnedPool = CreateChannel<long>(ReturnedPagesPoolSize);

        _allocatePagesTask = Task.Run(KeepAllocatingPages, CancellationToken);

        return 0;
    }

    // TODO: handle exception
    private async Task KeepAllocatingPages()
    {
        if (_allocatedPool == null || _returnedPool == null || _fileStream == null)
            ThrowNotStarted();

        byte[] freePages = _store.Get(StoreKey);
        _store.Remove(StoreKey);

        foreach (var page in EnumeratePagesFromStore(freePages))
            await _allocatedPool.Writer.WriteAsync(page, CancellationToken);

        while (!CancellationToken.IsCancellationRequested)
        {
            if (_returnedPool.Reader.TryRead(out long freePage))
            {
                await _allocatedPool.Writer.WriteAsync(freePage, CancellationToken);
                _pagesReused++;
            }
            else
            {
                await AllocatePages(_fileStream, _allocatedPool);
            }
        }
    }

    private async Task AllocatePages(FileStream fileStream, Channel<long> pool)
    {
        var missingCount = AllocatedPagesPoolSize - pool.Reader.Count;
        var allocateCount = Math.Max(1, Math.Min(missingCount / 2, MaxAllocateAtOnceSize / PageSize));

        long originalSize = fileStream.Length;
        long newSize = originalSize + PageSize * allocateCount;
        long pooledSize = originalSize;

        fileStream.SetLength(newSize);

        try
        {
            for (var page = originalSize; page < newSize; page += PageSize)
            {
                await pool.Writer.WriteAsync(page, CancellationToken);
                _pagesAllocated++;
                pooledSize += PageSize;
            }
        }
        finally
        {
            // Rollback length if some allocated pages didn't get to the pool
            if (pooledSize != newSize)
                fileStream.SetLength(pooledSize);
        }
    }

    private IEnumerable<long> EnumeratePagesFromStore(byte[]? bytes)
    {
        if (bytes == null)
            yield break;

        for (var i = bytes.Length; i > 0; i -= sizeof(long))
        {
            var page = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(i - sizeof(long)));
            yield return page;
        }
    }

    private byte[] GetPooledPagesBytes()
    {
        if (_allocatedPool == null || _returnedPool == null)
            ThrowNotStarted();

        var pages = new byte[(_allocatedPool.Reader.Count + _returnedPool.Reader.Count) * sizeof(long)];

        var i = -sizeof(long);

        while (_allocatedPool.Reader.TryRead(out var page))
            BinaryPrimitives.WriteInt64BigEndian(pages.AsSpan(i += sizeof(long)), page);

        while (_returnedPool.Reader.TryRead(out var page))
            BinaryPrimitives.WriteInt64BigEndian(pages.AsSpan(i += sizeof(long)), page);

        return pages;
    }

    private void StorePooledPages()
    {
        var bytes = GetPooledPagesBytes();
        _store.Set(StoreKey, bytes);
    }

    [DoesNotReturn]
    private static Exception ThrowNotStarted() => throw new InvalidOperationException("File pages pool has not started.");

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await StopAsync();

        _allocatePagesTask?.Dispose();
        _cancellationSource.Dispose();

        await (_fileStream?.DisposeAsync() ?? ValueTask.CompletedTask);
        _fileHandle?.Dispose();
    }
}
