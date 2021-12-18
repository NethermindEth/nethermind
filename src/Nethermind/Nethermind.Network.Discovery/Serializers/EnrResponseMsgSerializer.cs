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

using System.Net;
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
        // TODO: optimize
        byte[] data = Rlp.Encode(
            Rlp.Encode(msg.ExpirationTime)
        ).Bytes;

        byte[] serializedMsg = Serialize((byte)msg.MsgType, data);
        return serializedMsg;
    }

    public EnrResponseMsg Deserialize(byte[] msgBytes)
    {
        (PublicKey FarPublicKey, byte[] Mdc, byte[] Data) results = PrepareForDeserialization(msgBytes);
        RlpStream rlpStream = results.Data.AsRlpStream();
        rlpStream.ReadSequenceLength();
        rlpStream.DecodeKeccak(); // skip (not sure if needed to verify)
        
        string resHex = results.Data.Slice(rlpStream.Position).ToHexString();
        NodeRecord nodeRecord = _nodeRecordSigner.Deserialize(rlpStream);
        if (!_nodeRecordSigner.Verify(nodeRecord))
        {
            throw new NetworkingException("Invalid ENR signature", NetworkExceptionType.Discovery);
        }
        
        EnrResponseMsg msg = new(results.FarPublicKey, nodeRecord);
        return msg;
    }
}
