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

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Subprotocols.Messages;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Subprotocols.Serializers
{
    public class HiMessageSerializer : IMessageSerializer<HiMessage>
    {
        public byte[] Serialize(HiMessage message)
            => Rlp.Encode(Rlp.Encode(message.ProtocolVersion),
                Rlp.Encode(message.ProviderAddress),
                Rlp.Encode(message.ConsumerAddress),
                Rlp.Encode(message.NodeId.Bytes),
                Rlp.Encode(message.Signature.V),
                Rlp.Encode(message.Signature.R.WithoutLeadingZeros()),
                Rlp.Encode(message.Signature.S.WithoutLeadingZeros())).Bytes;

        public HiMessage Deserialize(byte[] bytes)
        {
            // try
            // {
                return Deserialize(bytes.AsRlpStream());
            // }
            // catch (Exception)
            // {
            //     // strange garbage from p2p
            //     return Deserialize(bytes.Skip(3).ToArray().AsRlpStream());
            // }
        }

        private static HiMessage Deserialize(RlpStream rlpStream)
        {
            rlpStream.ReadSequenceLength();
            var protocolVersion = rlpStream.DecodeByte();
            var providerAddress = rlpStream.DecodeAddress();
            var consumerAddress = rlpStream.DecodeAddress();
            var nodeId = new PublicKey(rlpStream.DecodeByteArray());
            var signature = SignatureDecoder.DecodeSignature(rlpStream);

            return new HiMessage(protocolVersion, providerAddress, consumerAddress,
                nodeId, signature);
        }
    }
}