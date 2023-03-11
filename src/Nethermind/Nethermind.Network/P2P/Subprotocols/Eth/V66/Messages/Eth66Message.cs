// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public abstract class Eth66Message<T> : P2PMessage where T : P2PMessage
    {
        public override int PacketType => EthMessage.PacketType;
        public override string Protocol => EthMessage.Protocol;
        public long RequestId { get; set; } = MessageConstants.Random.NextLong();
        public T EthMessage { get; set; }

        protected Eth66Message()
        {
        }

        protected Eth66Message(long requestId, T ethMessage)
        {
            RequestId = requestId;
            EthMessage = ethMessage;
        }

        public override string ToString()
            => $"{GetType().Name}Eth66({RequestId},{EthMessage})";
    }
}
