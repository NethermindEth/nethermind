// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public sealed class FindNodeMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IZeroInnerMessageSerializer<FindNodeMsg>
{
    public void Serialize(IByteBuffer byteBuffer, FindNodeMsg msg) => this.SerializeWithLengths(byteBuffer, msg);

    public void Serialize(IByteBuffer byteBuffer, FindNodeMsg msg, ref LengthStack lengths)
    {
        int length = lengths.Pop();

        byteBuffer.MarkIndex();
        PrepareBufferForSerialization(byteBuffer, length, (byte)msg.MsgType);
        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.StartSequence(lengths.Pop());
        writer.Encode(msg.SearchedNodeId);
        writer.Encode(msg.ExpirationTime);

        byteBuffer.ResetIndex();
        AddSignatureAndMdc(byteBuffer, length + 1);
    }

    public FindNodeMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey FarPublicKey, _, IByteBuffer Data) = PrepareForDeserialization(msgBytes);
        RlpReader ctx = new(Data.AsSpan());
        ctx.ReadSequenceLength();
        byte[] searchedNodeId = ctx.DecodeByteArray(NodeIdRlpLimit);
        long expirationTime = ctx.DecodeLong();

        Data.SetReaderIndex(Data.ReaderIndex + ctx.Position);
        FindNodeMsg findNodeMsg = new(FarPublicKey, expirationTime, searchedNodeId);
        return findNodeMsg;
    }

    public void PopulateLength(FindNodeMsg msg, ref LengthStack lengths)
    {
        int contentLength = Rlp.LengthOf(msg.SearchedNodeId);
        contentLength += Rlp.LengthOf(msg.ExpirationTime);

        lengths.Push(contentLength);
        lengths.Push(Rlp.LengthOfSequence(contentLength));
    }
}
