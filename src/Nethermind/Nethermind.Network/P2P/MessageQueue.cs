// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols;

namespace Nethermind.Network.P2P
{
    public class MessageQueue<TMsg, TData>(Action<TMsg> send)
        where TMsg : MessageBase
    {
        private bool _isClosed;
        private Request<TMsg, TData>? _currentRequest;
        private readonly Lock _lock = new();

        private readonly Queue<Request<TMsg, TData>> _requestQueue = new();

        public void Send(Request<TMsg, TData> request)
        {
            lock (_lock)
            {
                if (_isClosed)
                {
                    request.Message.TryDispose();
                    request.CompletionSource.TrySetCanceled();
                    return;
                }

                if (_currentRequest is null)
                {
                    _currentRequest = request;
                    _currentRequest.StartMeasuringTime();
                    send(_currentRequest.Message);
                }
                else
                {
                    _requestQueue.Enqueue(request);
                }
            }
        }

        public void Handle(TData data, long size)
        {
            lock (_lock)
            {
                if (_currentRequest is null)
                {
                    data.TryDispose();
                    throw new SubprotocolException($"Received a response to {nameof(TMsg)} that has not been requested");
                }

                _currentRequest.ResponseSize = size;
                if (!_currentRequest.CompletionSource.TrySetResult(data))
                {
                    data.TryDispose();
                }
                if (_requestQueue.TryDequeue(out _currentRequest))
                {
                    _currentRequest!.StartMeasuringTime();
                    send(_currentRequest.Message);
                }
            }
        }

        public void CompleteAdding()
        {
            lock (_lock)
            {
                _isClosed = true;

                if (_currentRequest is not null)
                {
                    _currentRequest.Message.TryDispose();
                    _currentRequest.CompletionSource.TrySetCanceled();
                    _currentRequest = null;
                }

                while (_requestQueue.TryDequeue(out Request<TMsg, TData>? request))
                {
                    request.Message.TryDispose();
                    request.CompletionSource.TrySetCanceled();
                }
            }
        }
    }
}
