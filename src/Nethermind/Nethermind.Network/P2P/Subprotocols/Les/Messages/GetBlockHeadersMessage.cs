// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class GetBlockHeadersMessage : P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.GetBlockHeaders;
        public override string Protocol { get; } = Contract.P2P.Protocol.Les;
        public long RequestId;
        public Eth.V62.Messages.GetBlockHeadersMessage EthMessage;

        public GetBlockHeadersMessage()
        {
        }

        public GetBlockHeadersMessage(Eth.V62.Messages.GetBlockHeadersMessage ethMessage, long requestId)
        {
            EthMessage = ethMessage;
            RequestId = requestId;
        }
    }
}
