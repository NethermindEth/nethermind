// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Core.Caching;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using TaskExtensions = Nethermind.Core.Extensions.TaskExtensions;

namespace Nethermind.Network.P2P;

public class MessageDictionary<T66Msg, TData>(ProtocolHandlerBase handler, TimeSpan? oldRequestThreshold = null, CancellationToken cancellationToken = default)
    where T66Msg : P2PMessage, IEth66Message
{
    // The limit is largely to prevent unexpected OOM.
    // But the side effect is that if the peer did not respond with the message, eventually it will throw
    // InvalidOperationException.
    private const int MaxConcurrentRequest = 32;
    private const int ExpiredRequestCacheCapacity = 128;

    // It could be that we had some kind of temporary connection loss, so once in a while we need to check really old
    // request. This is to prevent getting stuck on concurrent request limit and prevent potential memory leak.
    // The timeout is higher than Timeouts.Eth because it could be than the peer is delayed by only a few second.
    // If that is the case, and this throw due to unrecognized request id, the peer will get disconnected, which
    // we don't want to do too much as that decrease number of peers.
    private static readonly TimeSpan DefaultOldRequestThreshold = Timeouts.Cleanup;

    private readonly TimeSpan _oldRequestThreshold = oldRequestThreshold ?? DefaultOldRequestThreshold;
    private readonly Dictionary<long, Request<T66Msg, TData>> _requests = new(MaxConcurrentRequest);
    private ClockCache<long, byte>? _expiredRequests;
    private readonly Lock _lock = new();
    private readonly CancellationToken _cancellationToken = cancellationToken;
    private Task _cleanOldRequestTask = Task.CompletedTask;
    private int _requestCount;

    public void Send(Request<T66Msg, TData> request)
    {
        bool accepted = false;
        bool tooMany = false;

        using (Lock.Scope scope = _lock.EnterScope())
        {
            if (_requestCount >= MaxConcurrentRequest)
            {
                tooMany = true;
            }
            else if (!_requests.ContainsKey(request.Message.RequestId))
            {
                _expiredRequests?.Delete(request.Message.RequestId);
                _requests.Add(request.Message.RequestId, request);
                _requestCount++;
                request.StartMeasuringTime();
                accepted = true;
            }
        }

        if (!accepted)
        {
            request.Message.TryDispose();
            if (tooMany)
            {
                ThrowTooManyOutstandingRequests();
            }

            return;
        }

        handler.Send(request.Message);

        using (Lock.Scope scope = _lock.EnterScope())
        {
            if (_cleanOldRequestTask.IsCompleted)
            {
                _cleanOldRequestTask = CleanOldRequests();
            }
        }

        [StackTraceHidden, DoesNotReturn]
        static void ThrowTooManyOutstandingRequests() => throw new ConcurrencyLimitReachedException($"Concurrent request limit reached. Message type: {typeof(T66Msg)}");
    }

    private async Task CleanOldRequests()
    {
        while (true)
        {
            if (!await TaskExtensions.DelaySafe(_oldRequestThreshold, _cancellationToken))
                break;

            List<long> expiredRequestIds = [];
            using (Lock.Scope scope = _lock.EnterScope())
            {
                foreach (KeyValuePair<long, Request<T66Msg, TData>> requestIdValues in _requests)
                {
                    if (requestIdValues.Value.Elapsed > _oldRequestThreshold)
                    {
                        expiredRequestIds.Add(requestIdValues.Key);
                    }
                }
            }

            foreach (long requestId in expiredRequestIds)
            {
                TryExpireRequest(requestId);
            }

            using (Lock.Scope scope = _lock.EnterScope())
            {
                if (_requestCount == 0)
                {
                    _cleanOldRequestTask = Task.CompletedTask;
                    break;
                }
            }
        }
    }

    internal bool TryExpireRequest(long id)
    {
        Request<T66Msg, TData>? request;
        using (Lock.Scope scope = _lock.EnterScope())
        {
            if (!_requests.TryGetValue(id, out request) || request.Elapsed <= _oldRequestThreshold || !_requests.Remove(id))
            {
                return false;
            }

            _requestCount--;
            (_expiredRequests ??= new ClockCache<long, byte>(ExpiredRequestCacheCapacity, lockPartition: 1)).Set(id, 0);
        }

        request.Message.TryDispose();
        request.CompletionSource.TrySetException(new TimeoutException("No response received"));
        return true;
    }

    public void Handle(long id, TData data, long size)
    {
        Request<T66Msg, TData>? request = null;
        bool expired = false;

        using (Lock.Scope scope = _lock.EnterScope())
        {
            if (_requests.Remove(id, out request))
            {
                _requestCount--;
                request.ResponseSize = size;
            }
            else
            {
                expired = _expiredRequests?.Delete(id) == true;
            }
        }

        if (request is not null)
        {
            if (!request.CompletionSource.TrySetResult(data))
            {
                data.TryDispose();
            }

            return;
        }

        data.TryDispose();
        if (expired)
        {
            TraceLateResponse(id);
            return;
        }

        ThrowNotRequested();
    }

    /// <summary>
    /// Claims a response while retaining only the request lookup under the dictionary gate.
    /// </summary>
    /// <remarks>
    /// A known expired response consumes its one-use tombstone and can be discarded without deserializing the payload.
    /// </remarks>
    internal ResponseScope BeginResponse(IByteBuffer content)
    {
        using (Lock.Scope scope = _lock.EnterScope())
        {
            if (!Eth66RequestId.TryPeek(content, out long id))
            {
                ThrowNotRequested();
            }

            if (_requests.Remove(id, out Request<T66Msg, TData>? request))
            {
                _requestCount--;
                return new ResponseScope(request, id, expired: false);
            }

            if (_expiredRequests?.Delete(id) == true)
            {
                return new ResponseScope(null, id, expired: true);
            }

            ThrowNotRequested();
        }

        throw new InvalidOperationException();
    }

    /// <summary>
    /// Throws unless the response in <paramref name="content"/> carries the request id of a pending request.
    /// </summary>
    /// <remarks>
    /// Called before deserialization so an uncorrelated response is rejected without being decoded.
    /// The request stays pending; <see cref="Handle"/> still claims it once the response is decoded.
    /// </remarks>
    /// <exception cref="SubprotocolException">The request id is unreadable or not pending.</exception>
    public void ThrowIfNotRequested(IByteBuffer content) => GetPendingRequest(content);

    /// <summary>
    /// Returns the pending request that the response in <paramref name="content"/> answers.
    /// </summary>
    /// <remarks>
    /// Like <see cref="ThrowIfNotRequested"/>, called before deserialization; the request stays pending, so the
    /// response can be checked against it before <see cref="Handle"/> claims it.
    /// </remarks>
    /// <exception cref="SubprotocolException">The request id is unreadable or not pending.</exception>
    public T66Msg GetPendingRequest(IByteBuffer content)
    {
        using Lock.Scope scope = _lock.EnterScope();
        Request<T66Msg, TData>? request = null;
        if (!Eth66RequestId.TryPeek(content, out long id) || !_requests.TryGetValue(id, out request))
        {
            ThrowNotRequested();
        }

        return request.Message;
    }

    internal void TraceLateResponse(long id)
    {
        if (handler.Logger.IsTrace)
        {
            handler.Logger.Trace($"Ignored late {typeof(T66Msg).Name} response {id} from {handler.Session.Node:c}");
        }
    }

    internal ref struct ResponseScope
    {
        private readonly Request<T66Msg, TData>? _request;
        private readonly long _requestId;
        private readonly bool _expired;
        private bool _completed;
        private bool _disposed;
        private bool _disposeData;
        private TData _data;

        internal ResponseScope(Request<T66Msg, TData>? request, long requestId, bool expired)
        {
            _request = request;
            _requestId = requestId;
            _expired = expired;
            _completed = false;
            _disposed = false;
            _disposeData = false;
            _data = default!;
        }

        internal bool IsExpired => _expired;
        internal long RequestId => _requestId;
        internal T66Msg Message => _request?.Message ?? throw new InvalidOperationException("An expired response has no pending request");

        internal void Complete(long id, TData data, long size)
        {
            if (_completed || _disposed)
            {
                SetDataForDisposal(data);
                ThrowNotRequested();
            }

            if (_expired || _request is null || id != _requestId)
            {
                SetDataForDisposal(data);
                FailClaimedRequest();
                ThrowNotRequested();
            }

            _completed = true;
            _request.ResponseSize = size;
            if (!_request.CompletionSource.TrySetResult(data))
            {
                SetDataForDisposal(data);
            }
        }

        internal void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_completed && !_expired && _request is not null)
            {
                _completed = true;
                if (_request.CompletionSource.TrySetCanceled())
                {
                    _request.Message.TryDispose();
                }
            }

            if (_disposeData)
            {
                _disposeData = false;
                _data.TryDispose();
            }
        }

        private void FailClaimedRequest()
        {
            if (_request is not null)
            {
                _completed = true;
                if (_request.CompletionSource.TrySetException(new SubprotocolException("Response request ID did not match the claimed request")))
                {
                    _request.Message.TryDispose();
                }
            }
        }

        private void SetDataForDisposal(TData data)
        {
            if (_disposeData)
            {
                data.TryDispose();
                return;
            }

            _disposeData = true;
            _data = data;
        }
    }

    [StackTraceHidden, DoesNotReturn]
    private static void ThrowNotRequested() => throw new SubprotocolException($"Received a response to {typeof(T66Msg).Name} that has not been requested");
}
