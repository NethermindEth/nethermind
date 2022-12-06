// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
