// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.DebugModule;

internal sealed class TraceChainSubscription : Subscription, IPostAcknowledgementActivation
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SubscriptionManager _manager;
    private Func<TraceChainSubscription, CancellationToken, Task>? _produce;
    private Action? _returnLease;
    private IExclusiveRpcModulePool? _rentalPool;
    private Action<IRpcModule>? _returnRental;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Completion => _completion.Task;
    private bool _activated;
    private bool _producerFinished;
    private bool _cancellationFinished = true;
    private bool _finished;
    private bool _disposed;

    internal TraceChainSubscription(IJsonRpcDuplexClient client, SubscriptionManager manager, ILogger logger,
        Func<TraceChainSubscription, CancellationToken, Task> produce) : base(client, 1)
    {
        _manager = manager;
        _logger = logger;
        _produce = produce;
        client.Closed += OnClosed;
    }

    public override string Type => "traceChain";

    internal bool IsDisposed { get { lock (_gate) return _disposed; } }

    internal void ConfigureRental(IExclusiveRpcModulePool pool, Action<IRpcModule> returnRental)
    {
        lock (_gate)
        {
            if (_finished) return;
            _rentalPool = pool;
            _returnRental = returnRental;
        }
    }

    internal void OwnLease(Action returnLease)
    {
        bool finished;
        lock (_gate)
        {
            finished = _finished;
            if (!finished) _returnLease = returnLease;
        }
        if (finished) returnLease();
    }

    void IPostAcknowledgementActivation.Activate() => Activate();
    void IPostAcknowledgementActivation.Abort() => Abort();

    internal void Activate()
    {
        try
        {
            lock (_gate)
            {
                if (_finished || _disposed || _activated) return;
                _activated = true;
                // The lease stays owned until replay, sends and cancellation callbacks have exited.
                _ = Task.Run(RunAsync);
            }
        }
        catch
        {
            lock (_gate) _activated = false;
            Abort();
            throw;
        }
    }

    internal async Task SendAsync(TraceChainBlock result, CancellationToken token)
    {
        using JsonRpcResult message = CreateSubscriptionMessage(result, "debug_subscription");
        await JsonRpcDuplexClient.SendJsonRpcResult(message, token);
    }

    internal async Task<bool> ReplayBlockAsync(ulong number, bool isLast, TraceChainOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Action<IRpcModule> returnRental = _returnRental!;
        IRpcModule rented = await _rentalPool!.RentExclusive(token);
        bool transferred = false;
        try
        {
            OwnLease(() => returnRental(rented));
            transferred = true;
            token.ThrowIfCancellationRequested();
            DebugRpcModule module = rented as DebugRpcModule
                ?? throw new InvalidOperationException("The rented module does not support chain replay.");
            // Unlink the completed block and join any active parent callback before returning its module.
            using CancellationTokenSource blockCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            await module.TraceChainBlockAsync(this, number, isLast, options, blockCancellation.Token);
        }
        finally
        {
            if (!transferred) returnRental(rented);
            else ReturnBlockLease();
        }
        return !IsDisposed;
    }

    private void ReturnBlockLease()
    {
        Action? returnLease;
        lock (_gate)
        {
            // Cancellation callbacks may still touch the module; TryFinish returns it after they exit.
            if (_disposed) return;
            returnLease = _returnLease;
            _returnLease = null;
        }
        returnLease?.Invoke();
    }

    private async Task RunAsync()
    {
        try
        {
            CancellationToken token = _cancellation.Token;
            token.ThrowIfCancellationRequested();
            await _produce!(this, token);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            if (_logger.IsDebug) _logger.Debug($"{Type} subscription {Id} cancelled.");
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn) _logger.Warn($"{GetErrorMsg()} {exception.Message}");
        }
        finally
        {
            lock (_gate) _producerFinished = true;
            TryFinish();
        }
    }

    private void OnClosed(object? sender, EventArgs args) => Abort();

    internal void Abort()
    {
        Dispose();
        _manager.RemoveSubscription(JsonRpcDuplexClient, Id);
    }

    public override void Dispose()
    {
        lock (_gate)
        {
            if (_disposed || _finished) return;
            _disposed = true;
            _producerFinished |= !_activated;
            // Reserve CTS ownership before leaving the lock: replay can finish before CancelAsync starts.
            _cancellationFinished = false;
        }
        _ = CancelAsync();
    }

    private async Task CancelAsync()
    {
        try
        {
            await _cancellation.CancelAsync();
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn) _logger.Warn($"{GetErrorMsg()} Cancellation callback failed: {exception.Message}");
        }
        finally
        {
            lock (_gate) _cancellationFinished = true;
            TryFinish();
        }
    }

    private void TryFinish()
    {
        Action? returnLease;
        lock (_gate)
        {
            if (_finished || !_producerFinished || !_cancellationFinished) return;
            _finished = true;
            _produce = null;
            _rentalPool = null;
            _returnRental = null;
            returnLease = _returnLease;
            _returnLease = null;
        }
        _cancellation.Dispose();
        JsonRpcDuplexClient.Closed -= OnClosed;
        base.Dispose();
        try
        {
            returnLease?.Invoke();
        }
        finally
        {
            _completion.TrySetResult();
        }
    }
}

internal sealed class PendingTraceChainResponse(TraceChainSubscription subscription) : ResultWrapper<string>, IPostAcknowledgementResponse
{
    private bool _handedToSink;
    internal TraceChainSubscription Subscription { get; } = subscription;

    IPostAcknowledgementActivation IPostAcknowledgementResponse.TakeActivation() => TakeActivation();

    internal TraceChainSubscription TakeActivation()
    {
        _handedToSink = true;
        return Subscription;
    }

    internal override JsonRpcResponse WithResponseContext(in JsonRpcId id, Action? disposableAction)
    {
        _id = id;
        if (disposableAction is not null) AddDisposable(disposableAction);
        return this;
    }

    public override void Dispose()
    {
        if (!_handedToSink) Subscription.Abort();
        base.Dispose();
    }
}
