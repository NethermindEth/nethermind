// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetBlockHeadersMessage : Eth66Message<V62.Messages.GetBlockHeadersMessage>
    {
        public GetBlockHeadersMessage()
        {
        }

        public GetBlockHeadersMessage(long requestId, V62.Messages.GetBlockHeadersMessage ethMessage) : base(requestId, ethMessage)
        {
        }
    }
}
