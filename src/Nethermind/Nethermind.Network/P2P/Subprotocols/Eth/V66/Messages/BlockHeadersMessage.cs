// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class BlockHeadersMessage : Eth66Message<V62.Messages.BlockHeadersMessage>
    {
        public BlockHeadersMessage()
        {
        }

        public BlockHeadersMessage(long requestId, V62.Messages.BlockHeadersMessage ethMessage) : base(requestId, ethMessage)
        {
        }
    }
}
