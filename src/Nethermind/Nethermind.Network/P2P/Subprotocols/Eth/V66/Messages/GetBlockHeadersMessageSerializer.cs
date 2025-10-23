// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetBlockHeadersMessageSerializer : Eth66MessageSerializer<GetBlockHeadersMessage, V62.Messages.GetBlockHeadersMessage>
    {
        public GetBlockHeadersMessageSerializer() : base(new V62.Messages.GetBlockHeadersMessageSerializer())
        {
        }
    }
}
