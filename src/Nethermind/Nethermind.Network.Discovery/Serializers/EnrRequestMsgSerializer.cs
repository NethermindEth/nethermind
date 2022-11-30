// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class EnrRequestMsgSerializer : DiscoveryMsgSerializerBase, IMessageSerializer<EnrRequestMsg>
{
    public EnrRequestMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver)
        : base(ecdsa, nodeKey, nodeIdResolver) { }

    public byte[] Serialize(EnrRequestMsg msg)
    {
        // TODO: optimize
        byte[] data = Rlp.Encode(
            Rlp.Encode(msg.ExpirationTime)
        ).Bytes;

        byte[] serializedMsg = Serialize((byte)msg.MsgType, data);
        return serializedMsg;
    }

    public EnrRequestMsg Deserialize(byte[] msgBytes)
    {
        (PublicKey FarPublicKey, byte[] Mdc, byte[] Data) results = PrepareForDeserialization(msgBytes);
        RlpStream rlpStream = results.Data.AsRlpStream();

        rlpStream.ReadSequenceLength();
        long expirationTime = rlpStream.DecodeLong();

        EnrRequestMsg msg = new(results.FarPublicKey, expirationTime);
        return msg;
    }
}
