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
                            if (await follow(target, cancellation.Token)) break;
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException) { }
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception) { }
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
