// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class NodeDataMessageSerializer()
        : Eth66MessageSerializer<NodeDataMessage, V63.Messages.NodeDataMessage>(EthMessageSerializer), IOwningMessageSerializer<NodeDataMessage>
    {
        private static readonly V63.Messages.NodeDataMessageSerializer EthMessageSerializer = new();

        public NodeDataMessage Deserialize(IMemoryOwner<byte> owner, Memory<byte> data)
        {
            long requestId;
            int ethMessageStart;
            try
            {
                requestId = Eth66MessageEnvelope.ReadRequestId(data.Span, out ethMessageStart);
            }
            catch
            {
                owner.Dispose();
                throw;
            }

            return new NodeDataMessage(requestId, EthMessageSerializer.Deserialize(owner, data[ethMessageStart..]));
        }
    }
}
