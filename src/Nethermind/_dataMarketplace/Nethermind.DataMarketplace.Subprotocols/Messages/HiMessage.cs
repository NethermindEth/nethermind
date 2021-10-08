//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;

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
