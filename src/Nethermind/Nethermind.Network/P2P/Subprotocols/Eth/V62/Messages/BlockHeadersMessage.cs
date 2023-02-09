// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class BlockHeadersMessage : P2PMessage
    {
        public override int PacketType { get; } = Eth62MessageCode.BlockHeaders;
        public override string Protocol { get; } = "eth";

        public BlockHeader[] BlockHeaders { get; set; }

        public BlockHeadersMessage()
        {
        }

        public BlockHeadersMessage(BlockHeader[] blockHeaders)
        {
            BlockHeaders = blockHeaders;
        }

        public override string ToString() => $"{nameof(BlockHeadersMessage)}({BlockHeaders?.Length ?? 0})";
    }
}
