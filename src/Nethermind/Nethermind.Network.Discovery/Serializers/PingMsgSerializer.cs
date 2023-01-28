// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using DotNetty.Common.Utilities;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class PingMsgSerializer : DiscoveryMsgSerializerBase, IMessageSerializer<PingMsg>
{
    public PingMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver)
        : base(ecdsa, nodeKey, nodeIdResolver)
    {
    }

    public byte[] Serialize(PingMsg msg)
    {
        byte typeByte = (byte)msg.MsgType;
        Rlp source = Encode(msg.SourceAddress);
        Rlp destination = Encode(msg.DestinationAddress);

        byte[] data;
        if (msg.EnrSequence.HasValue)
        {
            data = Rlp.Encode(
                Rlp.Encode(msg.Version),
                source,
                destination,
                //verify if encoding is correct
                Rlp.Encode(msg.ExpirationTime),
                Rlp.Encode(msg.EnrSequence.Value)).Bytes;
        }
        else
        {
            data = Rlp.Encode(
                Rlp.Encode(msg.Version),
                source,
                destination,
                //verify if encoding is correct
                Rlp.Encode(msg.ExpirationTime)).Bytes;
        }

        byte[] serializedMsg = Serialize(typeByte, data);
        msg.Mdc = serializedMsg.Slice(0, 32);

        return serializedMsg;
    }

    public PingMsg Deserialize(byte[] msgBytes)
    {
        (PublicKey FarPublicKey, byte[] Mdc, byte[] Data) results = PrepareForDeserialization(msgBytes);
        RlpStream rlp = results.Data.AsRlpStream();
        rlp.ReadSequenceLength();
        int version = rlp.DecodeInt();

        rlp.ReadSequenceLength();
        ReadOnlySpan<byte> sourceAddress = rlp.DecodeByteArraySpan();

        // TODO: please note that we decode only one field for port and if the UDP is different from TCP then
        // our discovery messages will not be routed correctly (the fix will not be part of this commit)
        rlp.DecodeInt(); // UDP port
        int tcpPort = rlp.DecodeInt(); // we assume here that UDP and TCP port are same 

        IPEndPoint source = GetAddress(sourceAddress, tcpPort);
        rlp.ReadSequenceLength();
        ReadOnlySpan<byte> destinationAddress = rlp.DecodeByteArraySpan();
        IPEndPoint destination = GetAddress(destinationAddress, rlp.DecodeInt());
        rlp.DecodeInt(); // UDP port

        long expireTime = rlp.DecodeLong();
        PingMsg msg = new(results.FarPublicKey, expireTime, source, destination, results.Mdc);

        msg.Version = version;
        if (version == 4)
        {
            if (!rlp.HasBeenRead)
            {
                long enrSequence = rlp.DecodeLong();
                msg.EnrSequence = enrSequence;
            }
        }
        else
        {
            // what do we do when receive version 5?
        }

        return msg;
    }
}
