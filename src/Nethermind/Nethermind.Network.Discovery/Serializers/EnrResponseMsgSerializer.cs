// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class EnrResponseMsgSerializer : DiscoveryMsgSerializerBase, IMessageSerializer<EnrResponseMsg>
{
    private readonly NodeRecordSigner _nodeRecordSigner;

    public EnrResponseMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver)
        : base(ecdsa, nodeKey, nodeIdResolver)
    {
        _nodeRecordSigner = new NodeRecordSigner(ecdsa, nodeKey.Generate());
    }

    public byte[] Serialize(EnrResponseMsg msg)
    {
        int contentLength = Rlp.LengthOfKeccakRlp;
        contentLength += msg.NodeRecord.GetRlpLengthWithSignature();
        int totalLength = Rlp.LengthOfSequence(contentLength);

        RlpStream rlpStream = new(totalLength);
        rlpStream.StartSequence(contentLength);
        rlpStream.Encode(msg.RequestKeccak);
        msg.NodeRecord.Encode(rlpStream);

        byte[] serializedMsg = Serialize((byte)msg.MsgType, rlpStream.Data);
        return serializedMsg;
    }

    public EnrResponseMsg Deserialize(byte[] msgBytes)
    {
        (PublicKey? farPublicKey, _, byte[]? data) = PrepareForDeserialization(msgBytes);
        RlpStream rlpStream = data.AsRlpStream();
        rlpStream.ReadSequenceLength();
        Keccak? requestKeccak = rlpStream.DecodeKeccak(); // skip (not sure if needed to verify)

        int positionForHex = rlpStream.Position;
        NodeRecord nodeRecord = _nodeRecordSigner.Deserialize(rlpStream);
        if (!_nodeRecordSigner.Verify(nodeRecord))
        {
            string resHex = data.Slice(positionForHex).ToHexString();
            throw new NetworkingException($"Invalid ENR signature: {resHex}", NetworkExceptionType.Discovery);
        }

        EnrResponseMsg msg = new(farPublicKey, nodeRecord, requestKeccak!);
        return msg;
    }
}
