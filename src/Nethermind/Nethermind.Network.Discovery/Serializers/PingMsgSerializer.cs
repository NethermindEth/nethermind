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
using System.Net;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.P2P;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class PingMsgSerializer : DiscoveryMsgSerializerBase, IZeroInnerMessageSerializer<PingMsg>
{
    public PingMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver)
        : base(ecdsa, nodeKey, nodeIdResolver)
    {
    }

    public void Serialize(IByteBuffer byteBuffer, PingMsg msg)
    {
        (int totalLength, int contentLength, int sourceAddressLength, int destinationAddressLength) = GetLength(msg);

        byte[] array = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            RlpStream stream = new(array);

            byte typeByte = (byte)msg.MsgType;

            stream.StartSequence(contentLength);
            stream.Encode(msg.Version);
            Encode(stream, msg.SourceAddress, sourceAddressLength);
            Encode(stream, msg.DestinationAddress, destinationAddressLength);
            stream.Encode(msg.ExpirationTime);

            if (msg.EnrSequence.HasValue)
            {
                stream.Encode(msg.EnrSequence.Value);
            }

            Serialize(typeByte, stream.Data.AsSpan(0, totalLength), byteBuffer);
            byteBuffer.MarkReaderIndex();
            msg.Mdc = byteBuffer.Slice(0, 32).ReadAllBytesAsArray();
            byteBuffer.ResetReaderIndex();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }

    public PingMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey FarPublicKey, Memory<byte> Mdc, IByteBuffer Data) results = PrepareForDeserialization(msgBytes);
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
        PingMsg msg = new(results.FarPublicKey, expireTime, source, destination, results.Mdc.ToArray()) { Version = version };

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

    public int GetLength(PingMsg msg, out int contentLength)
    {
        (int totalLength, contentLength, int _, int _) =
            GetLength(msg);
        return totalLength;
    }


    private (int totalLength, int contentLength, int sourceAddressLength, int destinationAddressLength) GetLength(PingMsg msg)
    {
        int sourceAddressLength = GetIPEndPointLength(msg.SourceAddress);
        int destinationAddressLength = GetIPEndPointLength(msg.DestinationAddress);

         int contentLength = Rlp.LengthOf(msg.Version)
                        + Rlp.LengthOfSequence(sourceAddressLength)
                        + Rlp.LengthOfSequence(destinationAddressLength)
                        + Rlp.LengthOf(msg.ExpirationTime);

        if (msg.EnrSequence.HasValue)
        {
            contentLength += Rlp.LengthOf(msg.EnrSequence.Value);
        }

        return (Rlp.LengthOfSequence(contentLength), contentLength, sourceAddressLength, destinationAddressLength);
    }
}
