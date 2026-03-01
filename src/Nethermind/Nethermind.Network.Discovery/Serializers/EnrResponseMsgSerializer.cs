// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class EnrResponseMsgSerializer : DiscoveryMsgSerializerBase, IZeroInnerMessageSerializer<EnrResponseMsg>
{
    private readonly NodeRecordSigner _nodeRecordSigner;

    public EnrResponseMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver)
        : base(ecdsa, nodeKey, nodeIdResolver)
    {
        _nodeRecordSigner = new NodeRecordSigner(ecdsa, nodeKey.Generate());
    }

    public void Serialize(IByteBuffer byteBuffer, EnrResponseMsg msg)
    {
        int contentLength = Rlp.LengthOfKeccakRlp;
        contentLength += msg.NodeRecord.GetRlpLengthWithSignature();
        int totalLength = Rlp.LengthOfSequence(contentLength);

        byteBuffer.MarkIndex();
        PrepareBufferForSerialization(byteBuffer, totalLength, (byte)msg.MsgType);
        NettyRlpStream rlpStream = new(byteBuffer);
        rlpStream.StartSequence(contentLength);
        rlpStream.Encode(msg.RequestKeccak);
        msg.NodeRecord.Encode(rlpStream);

        byteBuffer.ResetIndex();
        AddSignatureAndMdc(byteBuffer, totalLength + 1);
    }

    public EnrResponseMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey? farPublicKey, _, IByteBuffer? data) = PrepareForDeserialization(msgBytes);
        Rlp.ValueDecoderContext ctx = data.AsRlpContext();
        ctx.ReadSequenceLength();
        Hash256? requestKeccak = ctx.DecodeKeccak(); // skip (not sure if needed to verify)

        int positionForHex = ctx.Position;
        NodeRecord nodeRecord = _nodeRecordSigner.Deserialize(ref ctx);
        if (!_nodeRecordSigner.Verify(nodeRecord))
        {
            string resHex = data.ReadBytes(positionForHex).ReadAllHex();
            throw new NetworkingException($"Invalid ENR signature: {resHex}", NetworkExceptionType.Discovery);
        }

        data.SetReaderIndex(data.ReaderIndex + ctx.Position);
        EnrResponseMsg msg = new(farPublicKey, nodeRecord, requestKeccak!);
        return msg;
    }

    public int GetLength(EnrResponseMsg msg, out int contentLength)
    {
        contentLength = Rlp.LengthOfKeccakRlp;
        contentLength += msg.NodeRecord.GetRlpLengthWithSignature();
        return Rlp.LengthOfSequence(contentLength);
    }
}
