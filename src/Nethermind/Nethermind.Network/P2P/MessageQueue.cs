// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Network.P2P.Subprotocols;

namespace Nethermind.Network.P2P
{
    public class MessageQueue<TMsg, TData> where TMsg : MessageBase
    {
        private bool _isClosed;
        private readonly Action<TMsg> _send;
        private Request<TMsg, TData>? _currentRequest;

        private readonly Queue<Request<TMsg, TData>> _requestQueue = new();

        public MessageQueue(Action<TMsg> send)
        {
            _send = send;
        }

        public void Send(Request<TMsg, TData> request)
        {
            if (_isClosed)
            {
                return;
            }

            lock (_requestQueue)
            {
                if (_currentRequest is null)
                {
                    _currentRequest = request;
                    _currentRequest.StartMeasuringTime();
                    _send(_currentRequest.Message);
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
                    throw new SubprotocolException($"Received a response to {nameof(TMsg)} that has not been requested");
                }

                _currentRequest.ResponseSize = size;
                _currentRequest.CompletionSource.SetResult(data);
                if (_requestQueue.TryDequeue(out _currentRequest))
                {
                    _currentRequest!.StartMeasuringTime();
                    _send(_currentRequest.Message);
                }
            }
        }

        public void CompleteAdding()
        {
            _isClosed = true;
        }
    }
}
