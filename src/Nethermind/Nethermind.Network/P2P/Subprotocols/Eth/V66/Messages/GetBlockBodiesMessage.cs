// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetBlockBodiesMessage : V62.Messages.GetBlockBodiesMessage, IEth66Message
    {
        public long RequestId { get; set; } = MessageConstants.Random.NextLong();

        public GetBlockBodiesMessage(long requestId, IReadOnlyList<Hash256> blockHashes)
            : base(blockHashes)
        {
            RequestId = requestId;
        }

        public GetBlockBodiesMessage(long requestId, V62.Messages.GetBlockBodiesMessage message)
            : this(requestId, message.BlockHashes)
        {
        }
    }
}
