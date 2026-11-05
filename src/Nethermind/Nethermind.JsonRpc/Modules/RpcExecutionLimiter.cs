// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;

namespace Nethermind.JsonRpc.Modules;

/// <summary>Queues execution-heavy RPCs before module rental without occupying worker threads.</summary>
public sealed class RpcExecutionLimiter
{
    private readonly SemaphoreSlim _semaphore;
    private readonly RpcLimits _limits;
    private readonly int _timeout;

    public RpcExecutionLimiter(IJsonRpcConfig config)
        : this(GetConcurrency(config), config.Timeout, RpcLimits.Default)
    {
    }

    internal RpcExecutionLimiter(int concurrency, int timeout, RpcLimits limits)
    {
        _semaphore = new SemaphoreSlim(concurrency, concurrency);
        _timeout = timeout;
        _limits = limits;
    }

    public static int GetConcurrency(IJsonRpcConfig config)
    {
        int processors = Environment.ProcessorCount;
        int requested = config.MaxConcurrentExecutionRequests ?? processors * 2;
        int concurrency = Math.Min(config.EthModuleConcurrentInstances ?? processors, requested > 0 ? requested : int.MaxValue);
        if (config.MaxConcurrentSharedRequests > 0)
        {
            // Reserve shared headroom whenever more than one shared request is permitted.
            concurrency = Math.Min(concurrency, Math.Max(1, config.MaxConcurrentSharedRequests / 2));
        }
        return Math.Max(1, concurrency);
    }

    public ValueTask EnterAsync() => _semaphore.Wait(0) ? ValueTask.CompletedTask : WaitAsync();

    private async ValueTask WaitAsync()
    {
        _limits.AcquireQueuedSlot();
        try
        {
            if (!await _semaphore.WaitAsync(_timeout))
            {
                throw new ModuleRentalTimeoutException("Unable to start RPC execution. Too many concurrent execution requests.");
            }
        }
        finally
        {
            _limits.DecrementQueuedCalls();
        }
    }

    public void Exit() => _semaphore.Release();
}
