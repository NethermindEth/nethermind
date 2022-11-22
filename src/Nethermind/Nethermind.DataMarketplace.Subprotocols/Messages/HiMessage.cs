// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class HiMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.Hi;
        public override string Protocol => "ndm";
        public byte ProtocolVersion { get; }
        public Address? ProviderAddress { get; }
        public Address? ConsumerAddress { get; }
        public PublicKey NodeId { get; }
        public Signature Signature { get; }

        public HiMessage(byte protocolVersion, Address? providerAddress, Address? consumerAddress, PublicKey nodeId,
            Signature signature)
        {
            ProtocolVersion = protocolVersion;
            ProviderAddress = providerAddress;
            ConsumerAddress = consumerAddress;
            NodeId = nodeId;
            Signature = signature;
        }
    }
}
