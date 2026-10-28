// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Caching.Memory;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.LightClient;

/// <summary>Retains verified payloads and shares bounded downloads independently of caller cancellation.</summary>
internal sealed class VerifiedPayloadCache<T>(Func<T, long> size, CancellationToken stoppingToken) : IAsyncDisposable
{
    internal const long Capacity = 32 * 1024 * 1024;
    internal const int MaxDownloads = 4;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = Capacity });
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    private readonly Lock _gate = new();
    private readonly Dictionary<Hash256, TaskCompletionSource<T>> _downloads = [];
    private TaskCompletionSource _changed = NewSignal();
    private bool _stopping;
    private Task? _dispose;

    internal async Task<T> GetAsync(Hash256 hash, Func<CancellationToken, Task<T>> fetch, CancellationToken cancellationToken)
    {
        CancellationToken stopping = default;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<T>? owner = null;
            Task<T>? result = null;
            Task changed;
            lock (_gate)
            {
                stopping.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_stopping, this);
                _stop.Token.ThrowIfCancellationRequested();
                stopping = _stop.Token;
                if (_cache.TryGetValue(hash, out T? cached)) return cached!;
                if (_downloads.TryGetValue(hash, out TaskCompletionSource<T>? download)) result = download.Task;
                else if (_downloads.Count < MaxDownloads)
                {
                    owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _downloads.Add(hash, owner);
                    result = owner.Task;
                }
                changed = _changed.Task;
            }
            if (owner is not null) _ = FetchAsync(hash, owner, fetch);
            using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
            if (result is not null) return await result.WaitAsync(waiting.Token);
            await changed.WaitAsync(waiting.Token);
        }
    }

    internal Task<T>[] GetManyAsync(Hash256[] hashes, Func<Hash256[], CancellationToken, Task<T>[]> fetch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hashes.Length, MaxDownloads);
        Task<T>[] result = new Task<T>[hashes.Length];
        using ArrayPoolList<(Hash256 Hash, TaskCompletionSource<T> Completion)> owners = new(hashes.Length);
        CancellationToken stopping;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            stopping = _stop.Token;
            stopping.ThrowIfCancellationRequested();
            for (int i = 0; i < hashes.Length; i++)
            {
                Hash256 hash = hashes[i];
                if (_cache.TryGetValue(hash, out T? cached)) result[i] = Task.FromResult(cached!);
                else if (_downloads.TryGetValue(hash, out TaskCompletionSource<T>? download)) result[i] = download.Task;
                else if (_downloads.Count < MaxDownloads)
                {
                    TaskCompletionSource<T> owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _downloads.Add(hash, owner);
                    owners.Add((hash, owner));
                    result[i] = owner.Task;
                }
            }
        }
        if (owners.Count != 0)
        {
            Hash256[] ownedHashes = new Hash256[owners.Count];
            for (int i = 0; i < owners.Count; i++) ownedHashes[i] = owners[i].Hash;
            Task<T>[] batch;
            try { batch = fetch(ownedHashes, stopping); }
            catch (Exception exception)
            {
                batch = new Task<T>[ownedHashes.Length];
                for (int i = 0; i < batch.Length; i++) batch[i] = Task.FromException<T>(exception);
            }
            for (int i = 0; i < owners.Count; i++)
            {
                int index = i;
                _ = FetchAsync(owners[i].Hash, owners[i].Completion, _ => batch[index]);
            }
        }
        for (int i = 0; i < result.Length; i++)
        {
            Hash256 hash = hashes[i];
            result[i] = result[i] is { } pending
                ? WaitAsync(pending)
                : GetAsync(hash, token => fetch([hash], token)[0], cancellationToken);
        }
        return result;

        async Task<T> WaitAsync(Task<T> pending)
        {
            using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
            return await pending.WaitAsync(waiting.Token);
        }
    }

    private async Task FetchAsync(Hash256 hash, TaskCompletionSource<T> completion, Func<CancellationToken, Task<T>> fetch)
    {
        T? result = default;
        Exception? failure = null;
        try
        {
            result = await fetch(_stop.Token);
            _stop.Token.ThrowIfCancellationRequested();
            long weight = Math.Max(1024, size(result));
            lock (_gate)
            {
                if (!_stopping && weight <= Capacity) _cache.Set(hash, result, new MemoryCacheEntryOptions { Size = weight });
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            lock (_gate)
            {
                _downloads.Remove(hash);
                _changed.TrySetResult();
                _changed = NewSignal();
                if (failure is OperationCanceledException && _stop.IsCancellationRequested) completion.TrySetCanceled(_stop.Token);
                else if (failure is not null)
                {
                    completion.TrySetException(failure);
                    // All callers may have canceled their waits before the shared download failed.
                    _ = completion.Task.Exception;
                }
                else completion.TrySetResult(result!);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_dispose ??= StopAsync());
    }

    private async Task StopAsync()
    {
        _stopping = true;
        _changed.TrySetResult();
        Task[] owners = _downloads.Values.Select(static download => (Task)download.Task).ToArray();
        await _stop.CancelAsync();
        await Task.WhenAll(owners).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _cache.Dispose();
        _stop.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
