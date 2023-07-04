// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class GetBlockBodiesMessage : P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.GetBlockBodies;
        public override string Protocol { get; } = Contract.P2P.Protocol.Les;
        public long RequestId;
        public Eth.V62.Messages.GetBlockBodiesMessage EthMessage;

        public GetBlockBodiesMessage()
        {
        }

        public GetBlockBodiesMessage(Eth.V62.Messages.GetBlockBodiesMessage ethMessage, long requestId)
        {
            EthMessage = ethMessage;
            RequestId = requestId;
        }
    }
}
