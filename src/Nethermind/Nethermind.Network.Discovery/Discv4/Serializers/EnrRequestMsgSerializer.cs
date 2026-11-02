// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public sealed class EnrRequestMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IMessageSerializer<EnrRequestMsg>
{
    public int Serialize(Span<byte> destination, EnrRequestMsg msg)
    {
        GetDataLength(msg, out int contentLength);

        RlpWriter writer = StartPacket(destination, (byte)msg.MsgType);
        writer.StartSequence(contentLength);
        writer.Encode(msg.ExpirationTime);

        int length = CompletePacket(destination, in writer, out ValueHash256 mdc);
        msg.Hash = mdc;
        return length;
    }

    public EnrRequestMsg Deserialize(ReadOnlySpan<byte> msgBytes)
    {
        (PublicKey farPublicKey, ValueHash256 mdc) = PrepareForDeserialization(msgBytes, out ReadOnlySpan<byte> data);
        RlpReader ctx = new(data);

        ctx.ReadSequenceLength();
        long expirationTime = ctx.DecodeLong();

        EnrRequestMsg msg = new(farPublicKey, mdc, expirationTime);
        return msg;
    }

    public int GetLength(EnrRequestMsg message) => GetPacketLength(GetDataLength(message, out _));

    private static int GetDataLength(EnrRequestMsg message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.ExpirationTime);
        return Rlp.LengthOfSequence(contentLength);
    }
}
