//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
                if (_currentRequest == null)
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
                if (_currentRequest == null)
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
