// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class NodeDataMessage : V63.Messages.NodeDataMessage, IEth66Message
    {
        public long RequestId { get; set; } = MessageConstants.Random.NextLong();

        public NodeDataMessage(long requestId, IByteArrayList? data)
            : base(data)
        {
            RequestId = requestId;
        }

        public NodeDataMessage(long requestId, V63.Messages.NodeDataMessage message)
            : this(requestId, message.Data)
        {
        }
    }
}
