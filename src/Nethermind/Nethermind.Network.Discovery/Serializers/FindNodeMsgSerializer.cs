// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class FindNodeMsgSerializer : DiscoveryMsgSerializerBase, IMessageSerializer<FindNodeMsg>
{
    public FindNodeMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver)
        : base(ecdsa, nodeKey, nodeIdResolver) { }

    public byte[] Serialize(FindNodeMsg msg)
    {
        byte[] data = Rlp.Encode(
            Rlp.Encode(msg.SearchedNodeId),
            //verify if encoding is correct
            Rlp.Encode(msg.ExpirationTime)
        ).Bytes;

        byte[] serializedMsg = Serialize((byte)msg.MsgType, data);
        return serializedMsg;
    }

    public FindNodeMsg Deserialize(byte[] msgBytes)
    {
        (PublicKey FarPublicKey, byte[] Mdc, byte[] Data) results = PrepareForDeserialization(msgBytes);
        RlpStream rlpStream = results.Data.AsRlpStream();

        rlpStream.ReadSequenceLength();
        byte[] searchedNodeId = rlpStream.DecodeByteArray();
        long expirationTime = rlpStream.DecodeLong();

        FindNodeMsg findNodeMsg = new(results.FarPublicKey, expirationTime, searchedNodeId);
        return findNodeMsg;
    }
}
