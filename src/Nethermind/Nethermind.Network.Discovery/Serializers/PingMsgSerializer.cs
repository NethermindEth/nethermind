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
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.P2P;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class PingMsgSerializer : DiscoveryMsgSerializerBase, IZeroMessageSerializer<PingMsg>
{
    public PingMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver)
        : base(ecdsa, nodeKey, nodeIdResolver)
    {
    }

    public void Serialize(IByteBuffer byteBuffer, PingMsg msg)
    {
        int length = GetLength(msg, out int contentLength);

        RlpStream stream = new(length);

        byte typeByte = (byte)msg.MsgType;

        stream.StartSequence(contentLength);
        stream.Encode(msg.Version);
        Encode(stream, msg.SourceAddress);
        Encode(stream, msg.DestinationAddress);
        stream.Encode(msg.ExpirationTime);

        if (msg.EnrSequence.HasValue)
        {
            stream.Encode(msg.EnrSequence.Value);
        }

        byte[] serializedMsg = Serialize(typeByte, stream.Data);
        msg.Mdc = serializedMsg.Slice(0, 32);
        byteBuffer.EnsureWritable(serializedMsg.Length);
        byteBuffer.WriteBytes(serializedMsg);
    }

    public PingMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey FarPublicKey, byte[] Mdc, IByteBuffer Data) results = PrepareForDeserialization(msgBytes);
        NettyRlpStream rlp = new (results.Data);
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

    private int GetLength(PingMsg msg, out int contentLength)
    {
        contentLength = Rlp.LengthOf(msg.Version)
                        + Rlp.LengthOfSequence(GetIPEndPointLength(msg.SourceAddress))
                        + Rlp.LengthOfSequence(GetIPEndPointLength(msg.DestinationAddress))
                        + Rlp.LengthOf(msg.ExpirationTime);

        if (msg.EnrSequence.HasValue)
        {
            contentLength += Rlp.LengthOf(msg.EnrSequence.Value);
        }

        return Rlp.LengthOfSequence(contentLength);
    }
}
