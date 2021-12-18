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
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class EnrResponseMsgSerializer : DiscoveryMsgSerializerBase, IMessageSerializer<EnrResponseMsg>
{
    public EnrResponseMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver)
        : base(ecdsa, nodeKey, nodeIdResolver) { }

    public byte[] Serialize(EnrResponseMsg msg)
    {
        // TODO: optimize
        byte[] data = Rlp.Encode(
            Rlp.Encode(msg.ExpirationTime)
        ).Bytes;

        byte[] serializedMsg = Serialize((byte) msg.MsgType, data);
        return serializedMsg;
    }

    public EnrResponseMsg Deserialize(byte[] msgBytes)
    {
        (PublicKey FarPublicKey, byte[] Mdc, byte[] Data) results = PrepareForDeserialization(msgBytes);
        RlpStream rlpStream = results.Data.AsRlpStream();

        rlpStream.ReadSequenceLength();
        rlpStream.DecodeKeccak(); // do I need to check it?
        int currentPosition = rlpStream.Position;
        int recordRlpLength = rlpStream.ReadSequenceLength();

        NodeRecord nodeRecord = new();
        
        // TODO: may want to move this deserialization logic to something reusable

        ReadOnlySpan<byte> sigBytes = rlpStream.DecodeByteArraySpan();
        // TODO: The recipient of the packet should verify that the node record is signed by node who sent ENRResponse.
        Signature signature = new(sigBytes, 0);
        int enrSequence = rlpStream.DecodeInt();
        while (rlpStream.Position < currentPosition + recordRlpLength)
        {
            string key = rlpStream.DecodeString();
            switch (key)
            {
                case EnrContentKey.Eth:
                    _ = rlpStream.ReadSequenceLength();
                    _ = rlpStream.ReadSequenceLength();
                    byte[] forkHash = rlpStream.DecodeByteArray();
                    int nextBlock = rlpStream.DecodeInt();
                    nodeRecord.SetEntry(new EthEntry(forkHash, nextBlock));
                    break;
                case EnrContentKey.Id:
                    rlpStream.SkipItem();
                    nodeRecord.SetEntry(IdEntry.Instance);
                    break;
                case EnrContentKey.Ip:
                    ReadOnlySpan<byte> ipBytes = rlpStream.DecodeByteArraySpan();
                    IPAddress address = new(ipBytes);
                    nodeRecord.SetEntry(new IpEntry(address));
                    break;
                case EnrContentKey.Tcp:
                    int tcpPort = rlpStream.DecodeInt();
                    nodeRecord.SetEntry(new TcpEntry(tcpPort));
                    break;
                case EnrContentKey.Udp:
                    int udpPort = rlpStream.DecodeInt();
                    nodeRecord.SetEntry(new UdpEntry(udpPort));
                    break;
                case EnrContentKey.Secp256K1:
                    ReadOnlySpan<byte> keyBytes = rlpStream.DecodeByteArraySpan();
                    CompressedPublicKey compressedPublicKey = new(keyBytes);
                    nodeRecord.SetEntry(new Secp256K1Entry(compressedPublicKey));
                    break;
                default:
                    rlpStream.SkipItem();
                    break;
            }
        }

        nodeRecord.Sequence = enrSequence;
        EnrResponseMsg msg = new (results.FarPublicKey, nodeRecord);
        return msg;
    }
}
