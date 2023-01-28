// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class PongMsgSerializer : DiscoveryMsgSerializerBase, IMessageSerializer<PongMsg>
{
    public PongMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : base(ecdsa, nodeKey, nodeIdResolver)
    {
    }

    public byte[] Serialize(PongMsg msg)
    {
        if (msg.FarAddress is null)
        {
            throw new NetworkingException($"Sending discovery message without {nameof(msg.FarAddress)} set.",
                NetworkExceptionType.Discovery);
        }

        byte[] data = Rlp.Encode(
            Encode(msg.FarAddress),
            Rlp.Encode(msg.PingMdc),
            Rlp.Encode(msg.ExpirationTime)
        ).Bytes;

        byte[] serializedMsg = Serialize((byte)msg.MsgType, data);
        return serializedMsg;
    }

    public PongMsg Deserialize(byte[] msgBytes)
    {
        (PublicKey FarPublicKey, byte[] Mdc, byte[] Data) results = PrepareForDeserialization(msgBytes);

        RlpStream rlp = results.Data.AsRlpStream();

        rlp.ReadSequenceLength();
        rlp.ReadSequenceLength();

        // GetAddress(rlp.DecodeByteArray(), rlp.DecodeInt());
        rlp.DecodeByteArraySpan();
        rlp.DecodeInt(); // UDP port (we ignore and take it from Netty)
        rlp.DecodeInt(); // TCP port
        byte[] token = rlp.DecodeByteArray();
        long expirationTime = rlp.DecodeLong();

        PongMsg msg = new(results.FarPublicKey, expirationTime, token);
        return msg;
    }
}
