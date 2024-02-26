// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class NodeDataMessage : P2PMessage
    {
        public IOwnedReadOnlyList<byte[]> Data { get; }
        public override int PacketType { get; } = Eth63MessageCode.NodeData;
        public override string Protocol { get; } = "eth";

        public NodeDataMessage(IOwnedReadOnlyList<byte[]>? data)
        {
            Data = data ?? ArrayPoolList<byte[]>.Empty();
        }

        public override string ToString() => $"{nameof(NodeDataMessage)}({Data.Count})";

        public override void Dispose()
        {
            base.Dispose();
            Data.Dispose();
        }
    }
}
