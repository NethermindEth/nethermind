// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols;

namespace Nethermind.Network.P2P
{
    public class MessageQueue<TMsg, TData>(Action<TMsg> send)
        where TMsg : MessageBase
    {
        private bool _isClosed;
        private Request<TMsg, TData>? _currentRequest;

        private readonly Queue<Request<TMsg, TData>> _requestQueue = new();

        public void Send(Request<TMsg, TData> request)
        {
            if (_isClosed)
            {
                request.Message.TryDispose();
                return;
            }

            lock (_requestQueue)
            {
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
            lock (_requestQueue)
            {
                if (_currentRequest is null)
                {
                    if (data is IDisposable d)
                    {
                        d.Dispose();
                    }

                    throw new SubprotocolException($"Received a response to {nameof(TMsg)} that has not been requested");
                }

                _currentRequest.ResponseSize = size;
                _currentRequest.CompletionSource.SetResult(data);
                if (_requestQueue.TryDequeue(out _currentRequest))
                {
                    _currentRequest!.StartMeasuringTime();
                    send(_currentRequest.Message);
                }
            }
        }

        public void CompleteAdding()
        {
            _isClosed = true;
        }
    }
}
