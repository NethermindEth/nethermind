// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class BlockHeadersMessage : P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.BlockHeaders;
        public override string Protocol { get; } = Contract.P2P.Protocol.Les;
        public Eth.V62.Messages.BlockHeadersMessage EthMessage { get; set; }
        public long RequestId { get; set; }
        public int BufferValue { get; set; }

        public BlockHeadersMessage()
        {
        }
        public BlockHeadersMessage(Eth.V62.Messages.BlockHeadersMessage ethMessage, long requestId, int bufferValue)
        {
            EthMessage = ethMessage;
            RequestId = requestId;
            BufferValue = bufferValue;
        }
    }
}
