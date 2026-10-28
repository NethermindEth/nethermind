// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public sealed class FindNodeMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IMessageSerializer<FindNodeMsg>
{
    public int Serialize(Span<byte> destination, FindNodeMsg msg)
    {
        GetDataLength(msg, out int contentLength);

        RlpWriter writer = StartPacket(destination, (byte)msg.MsgType);
        writer.StartSequence(contentLength);
        writer.Encode(msg.SearchedNodeId);
        writer.Encode(msg.ExpirationTime);

        return CompletePacket(destination, in writer, out _);
    }

    public FindNodeMsg Deserialize(ReadOnlySpan<byte> msgBytes)
    {
        (PublicKey FarPublicKey, _) = PrepareForDeserialization(msgBytes, out ReadOnlySpan<byte> data);
        RlpReader ctx = new(data);
        ctx.ReadSequenceLength();
        byte[] searchedNodeId = ctx.DecodeByteArray(NodeIdRlpLimit);
        long expirationTime = ctx.DecodeLong();

        FindNodeMsg findNodeMsg = new(FarPublicKey, expirationTime, searchedNodeId);
        return findNodeMsg;
    }

    public int GetLength(FindNodeMsg msg) => GetPacketLength(GetDataLength(msg, out _));

    private static int GetDataLength(FindNodeMsg msg, out int contentLength)
    {
        contentLength = Rlp.LengthOf(msg.SearchedNodeId);
        contentLength += Rlp.LengthOf(msg.ExpirationTime);

        return Rlp.LengthOfSequence(contentLength);
    }
}
