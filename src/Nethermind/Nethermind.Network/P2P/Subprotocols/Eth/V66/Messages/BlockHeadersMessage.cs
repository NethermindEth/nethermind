// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class BlockHeadersMessage : V62.Messages.BlockHeadersMessage, IEth66Message
    {
        public long RequestId { get; set; } = MessageConstants.Random.NextLong();

        public BlockHeadersMessage()
        {
        }

        public BlockHeadersMessage(long requestId, IOwnedReadOnlyList<BlockHeader>? blockHeaders)
            : base(blockHeaders)
        {
            RequestId = requestId;
        }

        public BlockHeadersMessage(long requestId, V62.Messages.BlockHeadersMessage message)
            : this(requestId, message.BlockHeaders)
        {
        }
    }
}
