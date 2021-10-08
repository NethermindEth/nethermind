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
using System.Text;

namespace Nethermind.Peering.Mothra
{
    public static class IMothraLibp2pExtensions
    {
        public static void SendGossip(this IMothraLibp2p mothraLibp2p, string topic, ReadOnlySpan<byte> data)
        {
            byte[] topicUtf8 = Encoding.UTF8.GetBytes(topic);
            mothraLibp2p.SendGossip(topicUtf8, data);
        }

        public static void SendRpcRequest(this IMothraLibp2p mothraLibp2p, string method, string peer,
            ReadOnlySpan<byte> data)
        {
            byte[] methodUtf8 = Encoding.UTF8.GetBytes(method);
            byte[] peerUtf8 = Encoding.UTF8.GetBytes(peer);
            mothraLibp2p.SendRpcRequest(methodUtf8, peerUtf8, data);
        }

        public static void SendRpcResponse(this IMothraLibp2p mothraLibp2p, string method, string peer,
            ReadOnlySpan<byte> data)
        {
            byte[] methodUtf8 = Encoding.UTF8.GetBytes(method);
            byte[] peerUtf8 = Encoding.UTF8.GetBytes(peer);
            mothraLibp2p.SendRpcResponse(methodUtf8, peerUtf8, data);
        }
    }
}