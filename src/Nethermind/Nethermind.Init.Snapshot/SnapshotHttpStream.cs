// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Init.Snapshot;

internal sealed record SnapshotStreamSettings(
    int Connections, int ChunkSize, TimeSpan InitialRetryDelay, TimeSpan MaxRetryDelay, TimeSpan StallTimeout, bool ComputeChecksum = false)
{
    public static SnapshotStreamSettings Default(int connections) =>
        new(connections, 64 * 1024 * 1024, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(300), SnapshotHttpClient.DefaultStallTimeout);
}

internal sealed class SnapshotHttpStream : Stream
{
    private const string ProgressLabel = "Snapshot streaming";
    private const int DrainBufferSize = 65536;
    private const int MaxRangeRejections = 5;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    private readonly SnapshotHttpClient _client;
    private readonly string _url;
    private readonly SnapshotRemoteInfo _remoteInfo;
    private readonly SnapshotStreamSettings _settings;
    private readonly ILogger _logger;
    private readonly IncrementalHash? _hasher;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _window;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<Chunk>> _pending = new();
    private readonly ConcurrentBag<byte[]> _buffers = [];
    private readonly ProgressReporter _progress;
    private readonly Task[] _producers;
    private readonly long _chunkCount;

    private Exception? _fault;
    private long _nextChunkToFetch = -1;
    private long _consumeIndex;
    private Chunk _current;
    private int _currentConsumed;
    private long _position;
    private bool _finished;
    private int _disposed;

    public SnapshotHttpStream(
        SnapshotHttpClient client,
        string url,
        SnapshotRemoteInfo remoteInfo,
        SnapshotStreamSettings settings,
        ILogManager logManager,
        CancellationToken cancellationToken)
    {
        _client = client;
        _url = url;
        _remoteInfo = remoteInfo;
        _settings = settings;
        _logger = logManager.GetClassLogger<SnapshotHttpStream>();
        _hasher = settings.ComputeChecksum ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool ranged = remoteInfo is { SupportsRanges: true, Length: not null };
        int producerCount = ranged ? settings.Connections : 1;
        _window = new SemaphoreSlim(producerCount + 1, producerCount + 1);
        _progress = new ProgressReporter(ProgressLabel, logManager, (ulong)(remoteInfo.Length ?? 0), ProgressInterval);
        _progress.Logger.SetFormat(SnapshotProgress.FormatBytes(ProgressLabel, remoteInfo.Length));

        if (ranged)
        {
            _chunkCount = (remoteInfo.Length!.Value + settings.ChunkSize - 1) / settings.ChunkSize;
            _producers = new Task[producerCount];
            for (int i = 0; i < producerCount; i++)
                _producers[i] = Task.Run(FetchChunksAsync);
        }
        else
        {
            _producers = [Task.Run(ReadSequentiallyAsync)];
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _remoteInfo.Length ?? throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || _finished)
            return 0;

        if (_current.Buffer is null && !TryTakeNextChunk())
            return 0;

        int toCopy = Math.Min(buffer.Length, _current.Length - _currentConsumed);
        _current.Buffer!.AsSpan(_currentConsumed, toCopy).CopyTo(buffer);
        _hasher?.AppendData(_current.Buffer!, _currentConsumed, toCopy);
        _currentConsumed += toCopy;
        _position += toCopy;
        _progress.Update((ulong)_position);

        if (_currentConsumed == _current.Length)
        {
            ReturnBuffer(_current.Buffer!);
            _current = default;
            _window.Release();
        }

        return toCopy;
    }

    public async Task<byte[]?> FinishAsync(CancellationToken cancellationToken)
    {
        await Task.Factory.StartNew(Drain, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        if (_remoteInfo.Length is long expected && _position != expected)
            throw new IOException($"Snapshot stream ended after {_position} bytes, expected {expected}.");
        return _hasher?.GetHashAndReset();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        await DisposeCoreAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DisposeCoreAsync().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        await Task.WhenAll(_producers).ConfigureAwait(false);
        _progress.Dispose();
        _hasher?.Dispose();
        _cts.Dispose();
        _window.Dispose();
        _buffers.Clear();
        _current = default;
        _pending.Clear();
    }

    private bool TryTakeNextChunk()
    {
        Chunk chunk = AwaitChunk(_consumeIndex);
        _pending.TryRemove(_consumeIndex, out _);
        _consumeIndex++;

        if (chunk.Buffer is null)
        {
            _finished = true;
            return false;
        }

        _current = chunk;
        _currentConsumed = 0;
        return true;
    }

    private Chunk AwaitChunk(long index)
    {
        try
        {
            return GetOrAddPending(index).Task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Exception? fault = Volatile.Read(ref _fault);
            if (fault is not null and not OperationCanceledException)
                ExceptionDispatchInfo.Capture(fault).Throw();
            throw;
        }
    }

    private void Drain()
    {
        byte[] scratch = ArrayPool<byte>.Shared.Rent(DrainBufferSize);
        try
        {
            while (Read(scratch, 0, DrainBufferSize) > 0)
            {
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private async Task FetchChunksAsync()
    {
        try
        {
            while (true)
            {
                await _window.WaitAsync(_cts.Token).ConfigureAwait(false);
                long index = Interlocked.Increment(ref _nextChunkToFetch);
                if (index >= _chunkCount)
                {
                    _window.Release();
                    if (index == _chunkCount)
                        Deliver(index, default);
                    return;
                }

                long offset = index * (long)_settings.ChunkSize;
                int length = (int)Math.Min(_settings.ChunkSize, _remoteInfo.Length!.Value - offset);
                byte[] buffer = RentBuffer();
                await FetchChunkAsync(offset, buffer, length).ConfigureAwait(false);
                Deliver(index, new Chunk(buffer, length));
            }
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private async Task FetchChunkAsync(long offset, byte[] buffer, int length)
    {
        using StallGuardedReader reader = new(_settings.StallTimeout, _cts.Token);
        TimeSpan retryDelay = _settings.InitialRetryDelay;
        int rangeRejections = 0;
        int received = 0;
        int receivedAtLastFailure = 0;

        while (true)
        {
            try
            {
                using HttpResponseMessage response = await _client.GetAsync(
                    _url, new RangeHeaderValue(offset + received, offset + length - 1), _remoteInfo.ETag, _cts.Token).ConfigureAwait(false);
                EnsureSourceUnchanged(response);

                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    if (++rangeRejections >= MaxRangeRejections)
                        throw new InvalidDataException(
                            $"Server repeatedly answered a range request with {(int)response.StatusCode}. Disable Snapshot.Streaming or use a server that supports range requests.");
                    throw new IOException($"Server answered a range request at {offset + received} with {(int)response.StatusCode}.");
                }

                long? from = response.Content.Headers.ContentRange?.From;
                if (from != offset + received)
                    throw new InvalidDataException($"Server returned a range starting at {from}, expected {offset + received}.");

                await using Stream content = await response.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
                while (received < length)
                {
                    int read = await reader.ReadAsync(content, buffer.AsMemory(received, length - received)).ConfigureAwait(false);
                    if (read == 0)
                        throw new IOException($"Connection ended {received} bytes into a {length} byte chunk at {offset}.");
                    received += read;
                }

                return;
            }
            catch (HttpRequestException e) when (SnapshotHttpClient.IsPermanentHttpError(e))
            {
                throw;
            }
            catch (Exception e) when (IsRetryable(e))
            {
                if (received > receivedAtLastFailure)
                {
                    receivedAtLastFailure = received;
                    retryDelay = _settings.InitialRetryDelay;
                }

                if (_logger.IsWarn)
                    _logger.Warn($"Snapshot chunk at {offset} failed after {received} bytes. Retrying in {retryDelay.TotalSeconds}s. Error: {e.Message}");
                await Task.Delay(retryDelay, _cts.Token).ConfigureAwait(false);
                retryDelay = Min(retryDelay * 2, _settings.MaxRetryDelay);
            }
        }
    }

    private async Task ReadSequentiallyAsync()
    {
        HttpResponseMessage? response = null;
        using StallGuardedReader reader = new(_settings.StallTimeout, _cts.Token);
        try
        {
            long produced = 0;
            long index = 0;
            int filled = 0;
            byte[] buffer = RentBuffer();
            TimeSpan retryDelay = _settings.InitialRetryDelay;
            Stream? content = null;

            while (true)
            {
                try
                {
                    if (content is null)
                        (response, content) = await ConnectSequentialAsync(produced + filled).ConfigureAwait(false);

                    int read = await reader.ReadAsync(content, buffer.AsMemory(filled, _settings.ChunkSize - filled)).ConfigureAwait(false);
                    if (read == 0)
                    {
                        if (_remoteInfo.Length is long expected && produced + filled < expected)
                            throw new IOException($"Connection ended after {produced + filled} bytes, expected {expected}.");
                        break;
                    }

                    retryDelay = _settings.InitialRetryDelay;
                    filled += read;
                    if (filled == _settings.ChunkSize)
                    {
                        await _window.WaitAsync(_cts.Token).ConfigureAwait(false);
                        Deliver(index++, new Chunk(buffer, filled));
                        produced += filled;
                        buffer = RentBuffer();
                        filled = 0;
                    }
                }
                catch (HttpRequestException e) when (SnapshotHttpClient.IsPermanentHttpError(e))
                {
                    throw;
                }
                catch (Exception e) when (IsRetryable(e))
                {
                    response?.Dispose();
                    response = null;
                    content = null;
                    if (_logger.IsWarn)
                        _logger.Warn($"Snapshot stream interrupted after {produced + filled} bytes. Retrying in {retryDelay.TotalSeconds}s. Error: {e.Message}");
                    await Task.Delay(retryDelay, _cts.Token).ConfigureAwait(false);
                    retryDelay = Min(retryDelay * 2, _settings.MaxRetryDelay);
                }
            }

            if (filled > 0)
            {
                await _window.WaitAsync(_cts.Token).ConfigureAwait(false);
                Deliver(index++, new Chunk(buffer, filled));
            }

            Deliver(index, default);
        }
        catch (Exception e)
        {
            Fail(e);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task<(HttpResponseMessage Response, Stream Content)> ConnectSequentialAsync(long skip)
    {
        RangeHeaderValue? range = skip > 0 ? new RangeHeaderValue(skip, null) : null;
        HttpResponseMessage response = await _client.GetAsync(_url, range, _remoteInfo.ETag, _cts.Token).ConfigureAwait(false);
        try
        {
            EnsureSourceUnchanged(response);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                return (response, Stream.Null);

            Stream content = await response.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                long? from = response.Content.Headers.ContentRange?.From;
                if (from != skip)
                    throw new InvalidDataException($"Server returned a range starting at {from}, expected {skip}.");
            }
            else if (skip > 0)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Server does not support range requests. Re-reading {skip} already received bytes to resume.");
                await SnapshotHttpClient.SkipAsync(content, skip, _settings.StallTimeout, _cts.Token).ConfigureAwait(false);
            }

            return (response, content);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private void EnsureSourceUnchanged(HttpResponseMessage response)
    {
        EntityTagHeaderValue? expected = _remoteInfo.ETag;
        EntityTagHeaderValue? actual = response.Headers.ETag;
        if (expected is not null && actual is not null && expected.Tag != actual.Tag)
            throw new SnapshotSourceChangedException($"Snapshot changed on the server during the download (ETag {expected.Tag} -> {actual.Tag}).");

        long? total = response.StatusCode switch
        {
            HttpStatusCode.PartialContent or HttpStatusCode.RequestedRangeNotSatisfiable => response.Content.Headers.ContentRange?.Length,
            HttpStatusCode.OK => response.Content.Headers.ContentLength,
            _ => null
        };
        if (total is not null && _remoteInfo.Length is not null && total != _remoteInfo.Length)
            throw new SnapshotSourceChangedException($"Snapshot size changed on the server during the download ({_remoteInfo.Length} -> {total} bytes).");
    }

    private void Deliver(long index, Chunk chunk) => GetOrAddPending(index).TrySetResult(chunk);

    private TaskCompletionSource<Chunk> GetOrAddPending(long index)
    {
        TaskCompletionSource<Chunk> tcs = _pending.GetOrAdd(
            index, static _ => new TaskCompletionSource<Chunk>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (Volatile.Read(ref _fault) is not null || _cts.IsCancellationRequested)
            tcs.TrySetCanceled();
        return tcs;
    }

    private void Fail(Exception e)
    {
        Interlocked.CompareExchange(ref _fault, e, null);
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        foreach (KeyValuePair<long, TaskCompletionSource<Chunk>> pending in _pending)
            pending.Value.TrySetCanceled();
    }

    private bool IsRetryable(Exception e) =>
        e is not SnapshotSourceChangedException
        && !_cts.IsCancellationRequested
        && e is IOException or HttpRequestException or OperationCanceledException;

    private byte[] RentBuffer() => _buffers.TryTake(out byte[]? buffer) ? buffer : new byte[_settings.ChunkSize];

    private void ReturnBuffer(byte[] buffer) => _buffers.Add(buffer);

    internal int PooledBufferCount => _buffers.Count;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private readonly record struct Chunk(byte[]? Buffer, int Length);
}
