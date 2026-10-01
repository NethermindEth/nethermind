// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Autofac.Features.AttributeFilters;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public class PingMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IZeroInnerMessageSerializer<PingMsg>
{
    protected virtual byte MsgTypeByte => (byte)MsgType.Ping;

    public void Serialize(IByteBuffer byteBuffer, PingMsg msg)
    {
        (int totalLength, int contentLength, int sourceAddressLength, int destinationAddressLength) = GetLength(msg);

        byteBuffer.MarkIndex();
        PrepareBufferForSerialization(byteBuffer, totalLength, MsgTypeByte);
        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.StartSequence(contentLength);
        writer.Encode(msg.Version);
        Encode(ref writer, msg.SourceAddress, msg.SourceTcpPort, sourceAddressLength);
        Encode(ref writer, msg.DestinationAddress, msg.DestinationTcpPort, destinationAddressLength);
        writer.Encode(msg.ExpirationTime);

        if (msg.EnrSequence.HasValue)
        {
            writer.Encode(msg.EnrSequence.Value);
        }

        byteBuffer.ResetIndex();
        AddSignatureAndMdc(byteBuffer, totalLength + 1);

        byteBuffer.MarkReaderIndex();
        msg.Mdc = ReadHash(byteBuffer, byteBuffer.ReaderIndex);
        byteBuffer.ResetReaderIndex();
    }

    public PingMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey FarPublicKey, ValueHash256 Mdc, IByteBuffer Data) = PrepareForDeserialization(msgBytes);
        RlpReader ctx = new(Data.AsSpan());
        int messageLength = ctx.ReadSequenceLength();
        if (messageLength > ctx.Length - ctx.Position)
        {
            throw new RlpException("Ping list exceeds packet data.");
        }

        int messageEnd = ctx.Position + messageLength;
        ReadOnlySpan<byte> versionBytes = ctx.DecodeByteArraySpan();
        int version = 0;
        if (versionBytes.Length <= sizeof(int))
        {
            foreach (byte value in versionBytes)
            {
                version = (version << 8) | value;
            }
        }

        ctx.ReadSequenceLength();
        ReadOnlySpan<byte> sourceAddress = ctx.DecodeByteArraySpan(IpAddressRlpLimit);

        int sourceUdpPort = ctx.DecodeInt();
        int sourceTcpPort = ctx.DecodeInt();

        IPEndPoint source = GetAddress(sourceAddress, sourceUdpPort, allowZeroPort: true);
        ctx.ReadSequenceLength();
        ReadOnlySpan<byte> destinationAddress = ctx.DecodeByteArraySpan(IpAddressRlpLimit);
        int destinationUdpPort = ctx.DecodeInt();
        int destinationTcpPort = ctx.DecodeInt();
        IPEndPoint destination = GetAddress(destinationAddress, destinationUdpPort, allowZeroPort: true);

        long expireTime = ctx.DecodeLong();
        PingMsg msg = new(FarPublicKey, expireTime, source, destination, Mdc, sourceTcpPort, destinationTcpPort) { Version = version };

        if (ctx.Position < messageEnd && IsNextEnrSequence(ctx))
        {
            msg.EnrSequence = ctx.DecodeULong();
        }

        while (ctx.Position < messageEnd)
        {
            int itemLength = ctx.PeekNextRlpLength();
            if (itemLength > messageEnd - ctx.Position)
            {
                throw new RlpException("Ping extra item exceeds its list.");
            }

            ctx.SkipBytes(itemLength);
        }

        ctx.Check(messageEnd);
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
        int sourceAddressLength = GetIPEndPointLength(msg.SourceAddress, msg.SourceTcpPort);
        int destinationAddressLength = GetIPEndPointLength(msg.DestinationAddress, msg.DestinationTcpPort);

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
