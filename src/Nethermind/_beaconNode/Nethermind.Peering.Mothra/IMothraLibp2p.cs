//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;

namespace Nethermind.Peering.Mothra
{
    public interface IMothraLibp2p
    {
        event GossipReceivedEventHandler? GossipReceived;
        event PeerDiscoveredEventHandler? PeerDiscovered;
        event RpcReceivedEventHandler? RpcReceived;
        bool IsStarted { get; }
        bool SendGossip(ReadOnlySpan<byte> topicUtf8, ReadOnlySpan<byte> data);
        bool SendRpcRequest(ReadOnlySpan<byte> methodUtf8, ReadOnlySpan<byte> peerUtf8, ReadOnlySpan<byte> data);
        bool SendRpcResponse(ReadOnlySpan<byte> methodUtf8, ReadOnlySpan<byte> peerUtf8, ReadOnlySpan<byte> data);
        void Start(MothraSettings settings);
    }
}