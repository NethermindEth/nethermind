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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P;

public class MessageDictionary<T66Msg, TMsg, TData> where T66Msg : Eth66Message<TMsg> where TMsg : P2PMessage
{
    private readonly Action<T66Msg> _send;

    // The limit is largely to prevent unexpected OOM.
    // But the side effect is that if the peer did not respond with the message, eventually it will throw
    // InvalidOperationException.
    private const int MaxConcurrentRequest = 32;

    // It could be that we had some kind of temporary connection loss, so once in a while we need to check really old
    // request. This is to prevent getting stuck on concurrent request limit and prevent potential memory leak.
    private static readonly TimeSpan DefaultOldRequestThreshold = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _oldRequestThreshold;

    private readonly ConcurrentDictionary<long, Request<T66Msg, TData>> _requests = new();
    private Task _cleanOldRequestTask = Task.CompletedTask;
    private int _requestCount = 0;

    public MessageDictionary(Action<T66Msg> send, TimeSpan? oldRequestThreshold = null)
    {
        _send = send;
        _oldRequestThreshold = oldRequestThreshold ?? DefaultOldRequestThreshold;
    }

    public void Send(Request<T66Msg, TData> request)
    {
        if (_requestCount >= MaxConcurrentRequest)
        {
            throw new InvalidOperationException("Concurrent request limit reached");
        }

        if (_requests.TryAdd(request.Message.RequestId, request))
        {
            _requestCount++;
            request.StartMeasuringTime();
            _send(request.Message);

            if (_cleanOldRequestTask.IsCompleted)
            {
                _cleanOldRequestTask = CleanOldRequests();
            }
        }
    }

    private async Task CleanOldRequests()
    {
        while (true)
        {
            await Task.Delay(_oldRequestThreshold);

            ArrayPoolList<long> toRemove = new(MaxConcurrentRequest);
            foreach (KeyValuePair<long, Request<T66Msg, TData>> requestIdValues in _requests)
            {
                if (requestIdValues.Value.Elapsed > _oldRequestThreshold)
                {
                    toRemove.Add(requestIdValues.Key);
                }
            }

            for (int i = 0; i < toRemove.Count; i++)
            {
                if (_requests.TryRemove(toRemove[i], out Request<T66Msg, TData> request))
                {
                    _requestCount--;
                    // Unblock waiting thread.
                    request.CompletionSource.SetException(new TimeoutException("No response received"));
                }
            }

            if (_requestCount == 0) break;
        }
    }

    public void Handle(long id, TData data, long size)
    {
        if (_requests.TryRemove(id, out Request<T66Msg, TData>? request))
        {
            _requestCount--;
            request.ResponseSize = size;
            request.CompletionSource.SetResult(data);
        }
        else
        {
            throw new SubprotocolException($"Received a response to {nameof(TMsg)} that has not been requested");
        }
    }
}
