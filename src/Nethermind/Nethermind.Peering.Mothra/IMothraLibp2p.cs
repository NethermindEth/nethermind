// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
