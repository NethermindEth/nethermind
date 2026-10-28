// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.LightClient;

/// <summary>Backfills and retains a bounded execution-header chain authenticated by finalized consensus.</summary>
internal sealed class VerifiedHeaderHistory : IAsyncDisposable
{
    internal const int PageSize = 128;
    internal const int WireBatchSize = 4 * PageSize;
    internal const int MaxHeaderBytes = 2048;
    private const int PrefetchPages = 3 * WireBatchSize / PageSize;
    internal readonly struct PageRequest(Task fetch, Func<BlockHeader, CancellationToken, Task<BlockHeader[]>> authenticate)
    {
        internal readonly Task Fetch = fetch;
        internal readonly Func<BlockHeader, CancellationToken, Task<BlockHeader[]>> Authenticate = authenticate;
    }
    private const long DiskWriteLimit = 1024L * 1024 * 1024;
    private static readonly byte[] IdentityKey = [0xff];
    private readonly IDb _db;
    private readonly HeaderDecoder _decoder;
    private readonly Func<VerifiedHead, CancellationToken, Task<BlockHeader>> _getAnchor;
    private readonly Func<BlockHeader, int, CancellationToken, Task<BlockHeader[]>> _fetchPage;
    private readonly Func<ulong, int, CancellationToken, PageRequest[]>? _prefetchPages;
    private readonly Nethermind.Logging.ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _runBudget;
    private readonly string? _directory;
    private readonly ValueHash256[] _hashes;
    private readonly Lock _state = new();
    private readonly SemaphoreSlim _access = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _changed = NewSignal();
    private readonly TaskCompletionSource _drained = NewSignal();
    private Task? _worker;
    private Task? _dispose;
    private int _calls;
    private bool _stopping;
    private string? _failure;
    private int _failureCode;
    private VerifiedHead? _target;
    private ulong _targetFirst;
    private VerifiedHead? _anchor;
    private ulong _first;
    private HistoryProgress _progress = new(null, null, null, null, false, null);

    internal HistoryProgress Progress => Volatile.Read(ref _progress);

    internal VerifiedHeaderHistory(IDb db, Hash256 genesis, ISpecProvider specProvider,
        Func<VerifiedHead, CancellationToken, Task<BlockHeader>> getAnchor,
        Func<BlockHeader, int, CancellationToken, Task<BlockHeader[]>> fetchPage, Nethermind.Logging.ILogger logger,
        TimeProvider? clock = null, int capacity = VerifiedLogHeaders.HistoryCapacity,
        TimeSpan? runBudget = null, string? directory = null,
        Func<ulong, int, CancellationToken, PageRequest[]>? prefetchPages = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, VerifiedLogHeaders.HistoryCapacity);
        _db = db;
        _decoder = new(specProvider);
        _getAnchor = getAnchor;
        _fetchPage = fetchPage;
        _prefetchPages = prefetchPages;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _runBudget = runBudget ?? TimeSpan.FromMinutes(30);
        _directory = directory;
        _hashes = new ValueHash256[capacity];
        byte[] identity = new byte[40];
        "LCH1"u8.CopyTo(identity);
        BinaryPrimitives.WriteInt32BigEndian(identity.AsSpan(4), capacity);
        genesis.Bytes.CopyTo(identity.AsSpan(8));
        Span<byte> saved = db.GetSpan(IdentityKey);
        try
        {
            if (!saved.IsEmpty && !saved.SequenceEqual(identity))
                throw new InvalidDataException("Log header cache belongs to another network or format.");
            if (saved.IsEmpty) db.Set(IdentityKey, identity);
        }
        finally { db.DangerousReleaseMemory(saved); }
    }

    internal static IDb OpenDb(string directory, ILogManager logs)
    {
        directory = Path.GetFullPath(directory);
        DbConfig config = new()
        {
            WriteAheadLogSync = true,
            MaxOpenFiles = 64,
            AdditionalRocksDbOptions = "write_buffer_size=1048576;max_write_buffer_number=2;max_total_wal_size=4194304;block_based_table_factory.block_cache=1048576;target_file_size_base=1048576;max_bytes_for_level_base=4194304;max_log_file_size=1048576;keep_log_file_num=2;",
        };
        return new DbOnTheRocks(directory, new DbSettings("lightClientLogHeaders", directory)
        {
            SkipMetricsTracking = true,
        }, config, new RocksDbConfigFactory(config, new PruningConfig(), new HardwareInfo(), logs,
            validateConfig: false), logs);
    }

    internal async Task<BlockHeader[]> GetHeadersAsync(VerifiedHead finalized, ulong first, ulong last,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (first > last || last > finalized.Number) throw new ArgumentOutOfRangeException(nameof(first));
        if (last - first >= VerifiedLogHeaders.MaxBlocks || finalized.Number - first >= (ulong)_hashes.Length)
            throw new RpcException(-32001, "Block is outside the verified log history window.");
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            Demand(finalized, first);
            _calls++;
        }
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            while (true)
            {
                Task changed;
                lock (_state)
                {
                    changed = _changed.Task;
                }
                await _access.WaitAsync(linked.Token);
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    BlockHeader[]? headers = ReadRange(finalized, first, last);
                    if (headers is not null) return headers;
                }
                finally { _access.Release(); }
                Task worker;
                lock (_state)
                {
                    if (_stopping) throw new OperationCanceledException(linked.Token);
                    if (_worker!.IsCompleted)
                    {
                        if (_failure is not null) throw new RpcException(_failureCode, _failure);
                        if (first < Floor(_target!.Number))
                            throw new RpcException(-32001, "Requested log history is outside the retained window.");
                        _worker = Task.Run(RunAsync);
                    }
                    worker = _worker;
                }
                await Task.WhenAny(changed, worker).WaitAsync(linked.Token);
            }
        }
        finally
        {
            lock (_state)
                if (--_calls == 0 && _stopping) _drained.TrySetResult();
        }
    }

    private void Demand(VerifiedHead finalized, ulong first)
    {
        if (_target is not null && finalized.Number == _target.Number && finalized.BlockHash != _target.BlockHash)
            throw new InvalidDataException("Log history demand conflicts with authenticated finality.");
        if (_target is null || finalized.Number > _target.Number)
            _target = finalized;
        ulong floor = Floor(_target.Number);
        _targetFirst = Math.Max(floor, _progress.TargetFirstBlock is null ? first : Math.Min(_targetFirst, first));
        if (_worker is null || _worker.IsCompleted)
        {
            _failure = null;
            _worker = Task.Run(RunAsync);
        }
        Volatile.Write(ref _progress, _progress with
        {
            TargetFirstBlock = _targetFirst, TargetLastBlock = _target.Number, IsBackfilling = true, Error = null,
        });
    }

    private async Task RunAsync()
    {
        using CancellationTokenSource timeout = new(_runBudget, _clock);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, timeout.Token);
        try
        {
            while (true)
            {
                VerifiedHead target;
                ulong first;
                lock (_state) { target = _target!; first = _targetFirst; }
                await AttachAsync(target, linked.Token);
                await BackfillAsync(first, linked.Token);
                await _access.WaitAsync(linked.Token);
                try
                {
                    lock (_state)
                    {
                        if (_target != target || _targetFirst < first || _anchor is null
                            || _anchor.BlockHash != target.BlockHash || _first > first) continue;
                        Publish(backfilling: false);
                        return;
                    }
                }
                finally { _access.Release(); }
            }
        }
        catch (Exception exception)
        {
            string failure = exception is OperationCanceledException
                ? "Log header backfill stopped before coverage completed."
                : "Unable to authenticate the requested log header history.";
            if (!_stop.IsCancellationRequested && _logger.IsWarn)
                _logger.Warn($"{failure} {exception.Message}");
            await _access.WaitAsync();
            try
            {
                lock (_state)
                {
                    _failure = failure;
                    _failureCode = exception is RpcException rpc ? rpc.Code : -32000;
                    Publish(backfilling: false);
                }
            }
            finally { _access.Release(); }
        }
    }

    private async Task AttachAsync(VerifiedHead target, CancellationToken cancellationToken)
    {
        await _access.WaitAsync(cancellationToken);
        VerifiedHead? previous;
        BlockHeader? cachedAnchor;
        try
        {
            previous = _anchor;
            if (previous?.BlockHash == target.BlockHash && previous.Number == target.Number) return;
            if (previous is not null && previous.Number > target.Number)
                throw new InvalidDataException("Log history anchor cannot move backwards.");
            if (previous is not null)
            {
                _first = Math.Max(_first, Floor(target.Number));
                if (_first > previous.Number) _anchor = null;
                lock (_state) Publish(backfilling: true);
            }
            cachedAnchor = ReadHeader(target.Number, target.BlockHash);
        }
        finally { _access.Release(); }

        BlockHeader cursor = cachedAnchor ?? await _getAnchor(target, cancellationToken);
        VerifiedLogHeaders.Verify(cursor, target.Number, target.BlockHash);
        if (cursor.StateRoot != target.StateRoot)
            throw new InvalidDataException("Finalized log anchor has an inconsistent state root.");
        await SaveAsync([cursor], cancellationToken, persist: cachedAnchor is null);
        if (previous is not null && target.Number - previous.Number < (ulong)_hashes.Length)
        {
            await foreach ((BlockHeader[] page, bool cached) in ReadPagesAsync(cursor, previous.Number, cancellationToken))
            {
                if (page[0].Number == previous.Number && page[0].Hash != previous.BlockHash)
                    throw new InvalidDataException("Finalized log ancestry conflicts with the existing anchor.");
                await SaveAsync(page, cancellationToken, persist: !cached);
                cursor = page[0];
            }
            if (cursor.Hash != previous.BlockHash)
                throw new InvalidDataException("Finalized log ancestry conflicts with the existing anchor.");
        }
        await _access.WaitAsync(cancellationToken);
        try
        {
            if (previous is null || target.Number - previous.Number >= (ulong)_hashes.Length)
                _first = target.Number;
            _anchor = target;
            lock (_state) Publish(backfilling: true);
        }
        finally { _access.Release(); }
    }

    private async Task BackfillAsync(ulong first, CancellationToken cancellationToken)
    {
        BlockHeader cursor;
        await _access.WaitAsync(cancellationToken);
        try
        {
            if (_first <= first) return;
            cursor = ReadHeader(_first, new Hash256(_hashes[Slot(_first)]))
                ?? throw new InvalidDataException("A verified log history cursor is corrupt.");
        }
        finally { _access.Release(); }
        await foreach ((BlockHeader[] page, bool cached) in ReadPagesAsync(cursor, first, cancellationToken))
        {
            await SaveAsync(page, cancellationToken, persist: !cached);
            await _access.WaitAsync(cancellationToken);
            try
            {
                _first = page[0].Number;
                lock (_state) Publish(backfilling: true);
            }
            finally { _access.Release(); }
        }
    }

    private async IAsyncEnumerable<(BlockHeader[] Headers, bool Cached)> ReadPagesAsync(BlockHeader cursor, ulong first,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Queue<(ulong Number, PageRequest Request)> pending = new(PrefetchPages);
        Task<BlockHeader[]>? active = null;
        Task? activeFetch = null;
        ulong next = cursor.Number;
        try
        {
            while (cursor.Number > first)
            {
                int count = (int)Math.Min(PageSize, cursor.Number - first);
                BlockHeader[]? page = null;
                bool cached = false;
                if (pending.TryPeek(out (ulong Number, PageRequest Request) queued) && queued.Number == cursor.Number)
                {
                    pending.Dequeue();
                    activeFetch = queued.Request.Fetch;
                    active = queued.Request.Authenticate(cursor, stopping.Token);
                }
                else
                {
                    page = await ReadCachedPageAsync(cursor, count, stopping.Token);
                    cached = page is not null;
                    if (!cached)
                    {
                        active = _fetchPage(cursor, count, stopping.Token);
                        activeFetch = active;
                    }
                }
                if (active is not null)
                {
                    if (pending.Count == 0) next = cursor.Number - (ulong)count;
                    while (_prefetchPages is not null && next > first)
                    {
                        int planned = (int)Math.Min(WireBatchSize, next - first);
                        if (pending.Count + (planned + PageSize - 1) / PageSize > PrefetchPages) break;
                        int prefetched = 0;
                        while (prefetched < planned)
                        {
                            int pageCount = Math.Min(PageSize, planned - prefetched);
                            if (await IsPagePresentAsync(next - (ulong)prefetched, pageCount, stopping.Token)) break;
                            prefetched += pageCount;
                        }
                        if (prefetched == 0) break;
                        foreach (PageRequest request in _prefetchPages(next, prefetched, stopping.Token))
                        {
                            pending.Enqueue((next, request));
                            int pageCount = Math.Min(PageSize, prefetched);
                            next -= (ulong)pageCount;
                            prefetched -= pageCount;
                        }
                    }
                    page = await active;
                    active = null;
                    activeFetch = null;
                }
                VerifyPage(cursor, count, page!);
                yield return (page!, cached);
                cursor = page![0];
            }
        }
        finally
        {
            try { await stopping.CancelAsync(); }
            finally
            {
                if (active is not null)
                    await ((Task)active).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (activeFetch is not null && !ReferenceEquals(activeFetch, active))
                    await activeFetch.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                foreach ((ulong _, PageRequest request) in pending)
                    await request.Fetch.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task<bool> IsPagePresentAsync(ulong number, int count, CancellationToken cancellationToken)
    {
        await _access.WaitAsync(cancellationToken);
        try
        {
            Span<byte> key = stackalloc byte[4];
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteInt32BigEndian(key, Slot(number - (ulong)count + (ulong)i));
                if (!_db.KeyExists(key)) return false;
            }
            return true;
        }
        finally { _access.Release(); }
    }

    private async Task<BlockHeader[]?> ReadCachedPageAsync(BlockHeader cursor, int count, CancellationToken cancellationToken)
    {
        BlockHeader[] page = new BlockHeader[count];
        Hash256 hash = cursor.ParentHash!;
        await _access.WaitAsync(cancellationToken);
        try
        {
            for (int i = count - 1; i >= 0; i--)
            {
                BlockHeader? header = ReadHeader(cursor.Number - (ulong)(count - i), hash);
                if (header is null) return null;
                page[i] = header;
                hash = header.ParentHash!;
            }
        }
        finally { _access.Release(); }
        return page;
    }

    internal static void VerifyPage(BlockHeader cursor, int count, BlockHeader[] page)
    {
        if (count is < 1 or > PageSize || page.Length != count || cursor.Number < (ulong)count)
            throw new InvalidDataException("Execution peer supplied an incomplete log header page.");
        Hash256 hash = cursor.ParentHash!;
        for (int i = count - 1; i >= 0; i--)
        {
            BlockHeader header = page[i];
            VerifiedLogHeaders.Verify(header, cursor.Number - (ulong)(count - i), hash);
            hash = header.ParentHash!;
        }
    }

    private async Task SaveAsync(BlockHeader[] headers, CancellationToken cancellationToken, bool persist = true)
    {
        byte[][] encoded = persist ? new byte[headers.Length][] : [];
        for (int i = 0; i < encoded.Length; i++)
        {
            if (_decoder.GetLength(headers[i], RlpBehaviors.None) > MaxHeaderBytes)
                throw new InvalidDataException("Execution header exceeds the log history record limit.");
            encoded[i] = _decoder.EncodeAsBytes(headers[i]);
        }
        await _access.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (persist)
            {
                CheckDiskBudget();
                using IWriteBatch batch = _db.StartWriteBatch();
                Span<byte> key = stackalloc byte[4];
                for (int i = 0; i < encoded.Length; i++)
                {
                    BinaryPrimitives.WriteInt32BigEndian(key, Slot(headers[i].Number));
                    batch.Set(key, encoded[i]);
                }
            }
            foreach (BlockHeader header in headers) _hashes[Slot(header.Number)] = header.Hash!.ValueHash256;
        }
        finally { _access.Release(); }
    }

    private BlockHeader[]? ReadRange(VerifiedHead finalized, ulong first, ulong last)
    {
        if (_anchor is null || _first > first || _anchor.Number < finalized.Number) return null;
        if (_hashes[Slot(finalized.Number)] != finalized.BlockHash.ValueHash256)
            throw new InvalidDataException("The requested log anchor conflicts with authenticated finality.");
        BlockHeader[] result = new BlockHeader[checked((int)(last - first + 1))];
        for (int i = 0; i < result.Length; i++)
        {
            ulong number = first + (ulong)i;
            BlockHeader? header = ReadHeader(number, new Hash256(_hashes[Slot(number)]));
            if (header is null)
            {
                _anchor = null;
                lock (_state) Publish(backfilling: true);
                return null;
            }
            result[i] = header;
        }
        return result;
    }

    private BlockHeader? ReadHeader(ulong number, Hash256 hash)
    {
        Span<byte> key = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, Slot(number));
        Span<byte> bytes = _db.GetSpan(key);
        try
        {
            if (bytes.Length is 0 or > MaxHeaderBytes || Keccak.Compute(bytes) != hash) return null;
            try
            {
                BlockHeader header = _decoder.DecodeCompleteNotNull(bytes);
                return header.Number == number && header.Hash == hash ? header : null;
            }
            catch (RlpException) { return null; }
        }
        finally { _db.DangerousReleaseMemory(bytes); }
    }

    private void CheckDiskBudget()
    {
        if (_directory is null) return;
        long bytes = 0;
        foreach (string file in Directory.EnumerateFiles(_directory))
        {
            try { bytes = checked(bytes + new FileInfo(file).Length); }
            catch (FileNotFoundException) { continue; }
            if (bytes > DiskWriteLimit)
                throw new RpcException(-32005, "Log header cache reached its disk write limit.");
        }
    }

    private int Slot(ulong number) => (int)(number % (ulong)_hashes.Length);
    private ulong Floor(ulong number) => number >= (ulong)_hashes.Length ? number - (ulong)_hashes.Length + 1 : 0;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Publish(bool backfilling)
    {
        Volatile.Write(ref _progress, new(_anchor is null ? null : _first, _anchor?.Number,
            _target is null ? null : _targetFirst, _target?.Number, backfilling, _failure));
        TaskCompletionSource changed = _changed;
        _changed = NewSignal();
        changed.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (_state)
        {
            if (_dispose is not null) return new(_dispose);
            _stopping = true;
            if (_calls == 0) _drained.TrySetResult();
            _dispose = DisposeCoreAsync(_worker);
            return new(_dispose);
        }
    }

    private async Task DisposeCoreAsync(Task? worker)
    {
        await _stop.CancelAsync();
        if (worker is not null) await worker;
        await _drained.Task;
        _db.Dispose();
        _access.Dispose();
        _stop.Dispose();
    }
}
