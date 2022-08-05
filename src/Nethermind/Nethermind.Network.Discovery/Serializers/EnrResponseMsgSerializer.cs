//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Buffers;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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

        byte[] array = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            RlpStream rlpStream = new(array);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(msg.RequestKeccak);
            msg.NodeRecord.Encode(rlpStream);
            Serialize((byte)msg.MsgType, rlpStream.Data.AsSpan(0, totalLength), byteBuffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
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
