// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class ReceiptsMessage : P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.Receipts;
        public override string Protocol { get; } = Contract.P2P.Protocol.Les;
        public long RequestId;
        public int BufferValue;
        public Eth.V63.Messages.ReceiptsMessage EthMessage;

        public ReceiptsMessage()
        {
        }

        public ReceiptsMessage(Eth.V63.Messages.ReceiptsMessage ethMessage, long requestId, int bufferValue)
        {
            EthMessage = ethMessage;
            RequestId = requestId;
            BufferValue = bufferValue;
        }
    }
}
