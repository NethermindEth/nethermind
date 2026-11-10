// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;

namespace Nethermind.JsonRpc.Modules;

/// <summary>Queues execution-heavy RPCs before module rental without occupying worker threads.</summary>
/// <remarks>Admits every call while <see cref="IJsonRpcConfig.MaxConcurrentExecutionRequests"/> is unset.</remarks>
public sealed class RpcExecutionLimiter
{
    private readonly SemaphoreSlim? _semaphore;
    private readonly RpcLimits _limits;
    private readonly int _timeout;

    public RpcExecutionLimiter(IJsonRpcConfig config)
        : this(GetConcurrency(config), config.Timeout, RpcLimits.Default)
    {
    }

    internal RpcExecutionLimiter(int? concurrency, int timeout, RpcLimits limits)
    {
        _semaphore = concurrency is { } permits ? new SemaphoreSlim(permits, permits) : null;
        _timeout = timeout;
        _limits = limits;
    }

    /// <summary>Returns the number of concurrent execution permits, or <see langword="null"/> when the limit is disabled.</summary>
    public static int? GetConcurrency(IJsonRpcConfig config)
    {
        if (config.MaxConcurrentExecutionRequests is not { } requested) return null;

        int concurrency = Math.Min(config.EthModuleConcurrentInstances ?? Environment.ProcessorCount, requested > 0 ? requested : int.MaxValue);
        if (config.MaxConcurrentSharedRequests > 0)
        {
            // Reserve shared headroom whenever more than one shared request is permitted.
            concurrency = Math.Min(concurrency, Math.Max(1, config.MaxConcurrentSharedRequests / 2));
        }
        return Math.Max(1, concurrency);
    }

    /// <summary>Acquires an execution permit, waiting asynchronously while none is free.</summary>
    /// <returns>A task that completes once the caller holds a permit.</returns>
    /// <remarks>
    /// Every admission that succeeds must be released by exactly one <see cref="Exit"/> call. An admission that
    /// throws holds no permit and must not be released. A caller whose execution continues after it returns, such
    /// as a streamed result, keeps the permit and releases it when that execution ends.
    /// </remarks>
    /// <exception cref="LimitExceededException">The queue of waiting requests is full.</exception>
    /// <exception cref="ModuleRentalTimeoutException">No permit became free within the request timeout.</exception>
    public ValueTask EnterAsync() => _semaphore is null || _semaphore.Wait(0) ? ValueTask.CompletedTask : WaitAsync();

    private async ValueTask WaitAsync()
    {
        _limits.AcquireQueuedSlot();
        try
        {
            if (!await _semaphore!.WaitAsync(_timeout))
            {
                throw new ModuleRentalTimeoutException("Unable to start RPC execution. Too many concurrent execution requests.");
            }
        }
        finally
        {
            _limits.DecrementQueuedCalls();
        }
    }

    /// <summary>Releases the permit a successful <see cref="EnterAsync"/> acquired.</summary>
    /// <remarks>Call it once per admission: a second call hands out a permit that nobody acquired.</remarks>
    public void Exit() => _semaphore?.Release();
}
