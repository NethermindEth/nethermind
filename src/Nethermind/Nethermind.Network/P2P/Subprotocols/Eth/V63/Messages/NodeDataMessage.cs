// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class NodeDataMessage(IByteArrayList? data) : P2PMessage
    {
        public IByteArrayList Data { get; } = data ?? EmptyByteArrayList.Instance;
        public override int PacketType => Eth63MessageCode.NodeData;
        public override string Protocol => "eth";

        public override string ToString() => $"{nameof(NodeDataMessage)}({Data.Count})";

        public override void Dispose()
        {
            base.Dispose();
            Data.Dispose();
        }
    }
}
