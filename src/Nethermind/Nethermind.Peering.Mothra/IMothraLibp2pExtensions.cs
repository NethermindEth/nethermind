// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
