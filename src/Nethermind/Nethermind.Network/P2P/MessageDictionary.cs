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
using Nethermind.Network.P2P.Subprotocols;

namespace Nethermind.Network.P2P;

public class MessageDictionary<TMsg, TData> where TMsg : MessageBase
{
    private readonly Action<TMsg> _send;

    private readonly ConcurrentDictionary<long, Request<TMsg, TData>> _requests = new();

    public MessageDictionary(Action<TMsg> send)
    {
        _send = send;
    }

    public void Send(Request<TMsg, TData> request)
    {
        if (_requests.TryAdd(request.Id, request))
        {
            _send(request.Message);
        }
    }

    public void Handle(long id, TData data, long size)
    {
        if (_requests.TryRemove(id, out Request<TMsg, TData>? request))
        {
            request.ResponseSize = size;
            request.CompletionSource.SetResult(data);
        }
        else
        {
            throw new SubprotocolException($"Received a response to {nameof(TMsg)} that has not been requested");
        }
    }
}
