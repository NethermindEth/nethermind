// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public sealed class FindNodeMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IZeroMessageSerializer<FindNodeMsg>
{
    public void Serialize(Span<byte> buffer, FindNodeMsg msg)
    {
        GetLength(msg, out int contentLength);

        PrepareBufferForSerialization(buffer, (byte)msg.MsgType);
        RlpWriter writer = new(buffer.Slice(EnvelopeLength));
        writer.StartSequence(contentLength);
        writer.Encode(msg.SearchedNodeId);
        writer.Encode(msg.ExpirationTime);

        AddSignatureAndMdc(buffer);
    }

    public FindNodeMsg Deserialize(ReadOnlySpan<byte> data, out int consumed)
    {
        (PublicKey FarPublicKey, _) = PrepareForDeserialization(data);
        ReadOnlySpan<byte> Data = data.Slice(EnvelopeLength);
        RlpReader ctx = new(Data);
        ctx.ReadSequenceLength();
        byte[] searchedNodeId = ctx.DecodeByteArray(NodeIdRlpLimit);
        long expirationTime = ctx.DecodeLong();

        consumed = EnvelopeLength + ctx.Position;
        FindNodeMsg findNodeMsg = new(FarPublicKey, expirationTime, searchedNodeId);
        return findNodeMsg;
    }

    public int GetLength(FindNodeMsg msg, out int contentLength)
    {
        contentLength = Rlp.LengthOf(msg.SearchedNodeId);
        contentLength += Rlp.LengthOf(msg.ExpirationTime);

        return EnvelopeLength + Rlp.LengthOfSequence(contentLength);
    }
}
