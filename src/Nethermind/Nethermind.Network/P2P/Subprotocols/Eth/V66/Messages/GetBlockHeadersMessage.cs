// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetBlockHeadersMessage : V62.Messages.GetBlockHeadersMessage, IEth66Message
    {
        public long RequestId { get; set; } = MessageConstants.Random.NextLong();

        public GetBlockHeadersMessage()
        {
        }

        public GetBlockHeadersMessage(long requestId, V62.Messages.GetBlockHeadersMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            RequestId = requestId;
            StartBlockNumber = message.StartBlockNumber;
            StartBlockHash = message.StartBlockHash;
            MaxHeaders = message.MaxHeaders;
            Skip = message.Skip;
            Reverse = message.Reverse;
        }
    }
}
