// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Serializes delayed BAL catch-up and cancels obsolete targets before starting their replacements.</summary>
public sealed class PbtBalFollowerScheduler(Func<BlockHeader, CancellationToken, Task<bool>> follow) : IAsyncDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task _pending = Task.CompletedTask;
    private bool _disposed;
    private string? _error;

    public string? Error => Volatile.Read(ref _error);

    public void Schedule(BlockHeader target)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cancellation?.Cancel();
            CancellationTokenSource cancellation = new();
            _cancellation = cancellation;
            Task previous = _pending;
            _pending = Task.Run(async () =>
            {
                await previous;
                try
                {
                    while (true)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        try
                        {
                            if (await follow(target, cancellation.Token))
                            {
                                Volatile.Write(ref _error, null);
                                break;
                            }
                            Volatile.Write(ref _error, "BAL catch-up is stalled; required canonical data is unavailable.");
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            Volatile.Write(ref _error, exception.Message);
                        }
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception) { Volatile.Write(ref _error, exception.Message); }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                        cancellation.Dispose();
                    }
                }
            });
        }
    }

    public void Cancel()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (_gate)
        {
            _disposed = true;
            _cancellation?.Cancel();
            pending = _pending;
        }
        await pending;
    }
}
