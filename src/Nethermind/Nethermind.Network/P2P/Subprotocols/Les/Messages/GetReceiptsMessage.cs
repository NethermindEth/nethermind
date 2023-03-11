// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class GetReceiptsMessage : P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.GetReceipts;
        public override string Protocol { get; } = Contract.P2P.Protocol.Les;
        public long RequestId;
        public Eth.V63.Messages.GetReceiptsMessage EthMessage;

        public GetReceiptsMessage()
        {
        }

        public GetReceiptsMessage(Eth.V63.Messages.GetReceiptsMessage ethMessage, long requestId)
        {
            EthMessage = ethMessage;
            RequestId = requestId;
        }
    }
}
