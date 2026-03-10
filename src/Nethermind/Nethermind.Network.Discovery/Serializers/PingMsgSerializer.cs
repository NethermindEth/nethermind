// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Autofac.Features.AttributeFilters;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class PingMsgSerializer : DiscoveryMsgSerializerBase, IZeroInnerMessageSerializer<PingMsg>
{
    public PingMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver)
        : base(ecdsa, nodeKey, nodeIdResolver)
    {
    }

    public void Serialize(IByteBuffer byteBuffer, PingMsg msg)
    {
        (int totalLength, int contentLength, int sourceAddressLength, int destinationAddressLength) = GetLength(msg);

        byteBuffer.MarkIndex();
        PrepareBufferForSerialization(byteBuffer, totalLength, (byte)msg.MsgType);
        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(contentLength);
        stream.Encode(msg.Version);
        Encode(stream, msg.SourceAddress, sourceAddressLength);
        Encode(stream, msg.DestinationAddress, destinationAddressLength);
        stream.Encode(msg.ExpirationTime);

        if (msg.EnrSequence.HasValue)
        {
            stream.Encode(msg.EnrSequence.Value);
        }

        byteBuffer.ResetIndex();
        AddSignatureAndMdc(byteBuffer, totalLength + 1);

        byteBuffer.MarkReaderIndex();
        msg.Mdc = byteBuffer.Slice(0, 32).ReadAllBytesAsArray();
        byteBuffer.ResetReaderIndex();
    }

    public PingMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey FarPublicKey, Memory<byte> Mdc, IByteBuffer Data) = PrepareForDeserialization(msgBytes);
        Rlp.ValueDecoderContext ctx = Data.AsRlpContext();
        ctx.ReadSequenceLength();
        int version = ctx.DecodeInt();

        ctx.ReadSequenceLength();
        ReadOnlySpan<byte> sourceAddress = ctx.DecodeByteArraySpan();

        // TODO: please note that we decode only one field for port and if the UDP is different from TCP then
        // our discovery messages will not be routed correctly (the fix will not be part of this commit)
        ctx.DecodeInt(); // UDP port
        int tcpPort = ctx.DecodeInt(); // we assume here that UDP and TCP port are same

        IPEndPoint source = GetAddress(sourceAddress, tcpPort);
        ctx.ReadSequenceLength();
        ReadOnlySpan<byte> destinationAddress = ctx.DecodeByteArraySpan();
        IPEndPoint destination = GetAddress(destinationAddress, ctx.DecodeInt());
        ctx.DecodeInt(); // UDP port

        long expireTime = ctx.DecodeLong();
        PingMsg msg = new(FarPublicKey, expireTime, source, destination, Mdc.ToArray()) { Version = version };

        if (version == 4)
        {
            if (ctx.Position < ctx.Length)
            {
                long enrSequence = ctx.DecodeLong();
                msg.EnrSequence = enrSequence;
            }
        }
        else
        {
            // what do we do when receive version 5?
        }

        Data.SetReaderIndex(Data.ReaderIndex + ctx.Position);
        return msg;
    }

    public int GetLength(PingMsg msg, out int contentLength)
    {
        (int totalLength, contentLength, int _, int _) =
            GetLength(msg);
        return totalLength;
    }


    private static (int totalLength, int contentLength, int sourceAddressLength, int destinationAddressLength) GetLength(PingMsg msg)
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
