// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class BlockBodiesMessage : P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.BlockBodies;
        public override string Protocol { get; } = Contract.P2P.Protocol.Les;
        public Eth.V62.Messages.BlockBodiesMessage EthMessage { get; set; }
        public long RequestId { get; set; }
        public int BufferValue { get; set; }

        public BlockBodiesMessage()
        {
        }

        public BlockBodiesMessage(Eth.V62.Messages.BlockBodiesMessage ethMessage, long requestId, int bufferValue)
        {
            EthMessage = ethMessage;
            RequestId = requestId;
            BufferValue = bufferValue;
        }
    }
}
