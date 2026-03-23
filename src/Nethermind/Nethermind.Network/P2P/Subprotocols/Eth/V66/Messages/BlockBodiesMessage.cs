// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class BlockBodiesMessage : V62.Messages.BlockBodiesMessage, IEth66Message
    {
        public long RequestId { get; set; } = MessageConstants.Random.NextLong();

        public BlockBodiesMessage()
        {
        }

        public BlockBodiesMessage(long requestId, OwnedBlockBodies? bodies)
        {
            RequestId = requestId;
            Bodies = bodies;
        }

        public BlockBodiesMessage(long requestId, V62.Messages.BlockBodiesMessage message)
            : this(requestId, message.Bodies)
        {
        }
    }
}
