// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Enr;
using Nethermind.Network.P2P;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class EnrResponseMsgSerializer : DiscoveryMsgSerializerBase, IZeroInnerMessageSerializer<EnrResponseMsg>
{
    private readonly NodeRecordSigner _nodeRecordSigner;

    public EnrResponseMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver)
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
        NettyRlpStream rlpStream = new(data);
        rlpStream.ReadSequenceLength();
        Keccak? requestKeccak = rlpStream.DecodeKeccak(); // skip (not sure if needed to verify)

        int positionForHex = rlpStream.Position;
        NodeRecord nodeRecord = _nodeRecordSigner.Deserialize(rlpStream);
        if (!_nodeRecordSigner.Verify(nodeRecord))
        {
            string resHex = data.ReadBytes(positionForHex).ReadAllHex();
            throw new NetworkingException($"Invalid ENR signature: {resHex}", NetworkExceptionType.Discovery);
        }

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
