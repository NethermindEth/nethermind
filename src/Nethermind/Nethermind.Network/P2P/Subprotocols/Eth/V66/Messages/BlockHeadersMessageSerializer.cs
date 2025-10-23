// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class BlockHeadersMessageSerializer : Eth66MessageSerializer<BlockHeadersMessage, V62.Messages.BlockHeadersMessage>
    {
        public BlockHeadersMessageSerializer() : base(new V62.Messages.BlockHeadersMessageSerializer())
        {
        }
    }
}
