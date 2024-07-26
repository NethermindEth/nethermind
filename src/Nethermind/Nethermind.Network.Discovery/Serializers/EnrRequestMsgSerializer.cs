// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class EnrRequestMsgSerializer : DiscoveryMsgSerializerBase, IZeroInnerMessageSerializer<EnrRequestMsg>
{
    public EnrRequestMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver)
        : base(ecdsa, nodeKey, nodeIdResolver) { }

    public void Serialize(IByteBuffer byteBuffer, EnrRequestMsg msg)
    {
        int length = GetLength(msg, out int contentLength);

        byteBuffer.MarkIndex();
        PrepareBufferForSerialization(byteBuffer, length, (byte)msg.MsgType);
        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(contentLength);
        stream.Encode(msg.ExpirationTime);

        byteBuffer.ResetIndex();

        AddSignatureAndMdc(byteBuffer, length + 1);
    }

    public EnrRequestMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey FarPublicKey, Memory<byte> Mdc, IByteBuffer Data) results = PrepareForDeserialization(msgBytes);
        NettyRlpStream rlpStream = new(results.Data);

        rlpStream.ReadSequenceLength();
        long expirationTime = rlpStream.DecodeLong();

        EnrRequestMsg msg = new(results.FarPublicKey, expirationTime);
        return msg;
    }

    public int GetLength(EnrRequestMsg message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.ExpirationTime);
        return Rlp.LengthOfSequence(contentLength);
    }
}
