// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class NodeDataMessage : P2PMessage
    {
        public byte[][] Data { get; }
        public override int PacketType { get; } = Eth63MessageCode.NodeData;
        public override string Protocol { get; } = "eth";

        public NodeDataMessage(byte[][]? data)
        {
            Data = data ?? Array.Empty<byte[]>();
        }

        public override string ToString() => $"{nameof(NodeDataMessage)}({Data.Length})";
    }
}
