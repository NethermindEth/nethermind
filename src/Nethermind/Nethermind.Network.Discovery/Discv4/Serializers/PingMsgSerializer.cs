// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using Autofac.Features.AttributeFilters;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public class PingMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IZeroMessageSerializer<PingMsg>
{
    protected virtual byte MsgTypeByte => (byte)MsgType.Ping;

    public void Serialize(Span<byte> buffer, PingMsg msg)
    {
        (int _, int contentLength, int sourceAddressLength, int destinationAddressLength) = GetLength(msg);

        PrepareBufferForSerialization(buffer, MsgTypeByte);
        RlpWriter writer = new(buffer.Slice(EnvelopeLength));
        writer.StartSequence(contentLength);
        writer.Encode(msg.Version);
        Encode(ref writer, msg.SourceAddress, msg.SourceTcpPort, sourceAddressLength);
        Encode(ref writer, msg.DestinationAddress, msg.DestinationTcpPort, destinationAddressLength);
        writer.Encode(msg.ExpirationTime);

        if (msg.EnrSequence.HasValue)
        {
            writer.Encode(msg.EnrSequence.Value);
        }

        AddSignatureAndMdc(buffer);

        msg.Mdc = new ValueHash256(buffer.Slice(0, Hash256.Size));
    }

    public PingMsg Deserialize(ReadOnlySpan<byte> data, out int consumed)
    {
        (PublicKey FarPublicKey, ValueHash256 Mdc) = PrepareForDeserialization(data);
        ReadOnlySpan<byte> Data = data.Slice(EnvelopeLength);
        RlpReader ctx = new(Data);
        int messageLength = ctx.ReadSequenceLength();
        if (messageLength > ctx.Length - ctx.Position)
        {
            throw new RlpException("Ping list exceeds packet data.");
        }

        int messageEnd = ctx.Position + messageLength;
        ReadOnlySpan<byte> versionBytes = ctx.DecodeByteArraySpan();
        int version = 0;
        if (versionBytes.Length < sizeof(int) ||
            (versionBytes.Length == sizeof(int) && versionBytes[0] < 0x80))
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
            ctx.SkipItem();
        }

        ctx.Check(messageEnd);
        consumed = EnvelopeLength + ctx.Position;
        return msg;
    }

    public int GetLength(PingMsg msg, out int contentLength)
    {
        (int totalLength, contentLength, int _, int _) =
            GetLength(msg);
        return EnvelopeLength + totalLength;
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
