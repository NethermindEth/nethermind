// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Autofac.Features.AttributeFilters;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public class PingMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IMessageSerializer<PingMsg>
{
    protected virtual byte MsgTypeByte => (byte)MsgType.Ping;

    public int Serialize(Span<byte> destination, PingMsg msg)
    {
        (_, int contentLength, int sourceAddressLength, int destinationAddressLength) = GetDataLength(msg);

        RlpWriter writer = StartPacket(destination, MsgTypeByte);
        RlpSequence sequence = writer.StartSequence(contentLength);
        writer.Encode(msg.Version);
        Encode(ref writer, msg.SourceAddress, msg.SourceTcpPort, sourceAddressLength);
        Encode(ref writer, msg.DestinationAddress, msg.DestinationTcpPort, destinationAddressLength);
        writer.Encode(msg.ExpirationTime);

        if (msg.EnrSequence.HasValue)
        {
            writer.Encode(msg.EnrSequence.Value);
        }

        writer.EndSequence(sequence);
        int length = CompletePacket(destination, in writer, out ValueHash256 mdc);
        msg.Mdc = mdc;
        return length;
    }

    public PingMsg Deserialize(ReadOnlySpan<byte> msgBytes)
    {
        (PublicKey FarPublicKey, ValueHash256 Mdc) = PrepareForDeserialization(msgBytes, out ReadOnlySpan<byte> data);
        RlpReader ctx = new RlpReader(data).EnterSequence(out _);
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

        RlpReader sourceReader = ctx.EnterSequence(out int sourceLength);
        ReadOnlySpan<byte> sourceAddress = sourceReader.DecodeByteArraySpan(IpAddressRlpLimit);

        int sourceUdpPort = sourceReader.DecodeInt();
        int sourceTcpPort = sourceReader.DecodeInt();
        ctx.ExitSequenceIgnoringRest(sourceReader, sourceLength);

        IPEndPoint source = GetAddress(sourceAddress, sourceUdpPort, allowZeroPort: true);
        RlpReader destinationReader = ctx.EnterSequence(out int destinationLength);
        ReadOnlySpan<byte> destinationAddress = destinationReader.DecodeByteArraySpan(IpAddressRlpLimit);
        int destinationUdpPort = destinationReader.DecodeInt();
        int destinationTcpPort = destinationReader.DecodeInt();
        ctx.ExitSequenceIgnoringRest(destinationReader, destinationLength);
        IPEndPoint destination = GetAddress(destinationAddress, destinationUdpPort, allowZeroPort: true);

        long expireTime = ctx.DecodeLong();
        PingMsg msg = new(FarPublicKey, expireTime, source, destination, Mdc, sourceTcpPort, destinationTcpPort) { Version = version };

        if (ctx.HasNext && IsNextEnrSequence(ctx))
        {
            msg.EnrSequence = ctx.DecodeULong();
        }

        while (ctx.HasNext)
        {
            ctx.SkipItem();
        }

        return msg;
    }

    public int GetLength(PingMsg msg) => GetPacketLength(GetDataLength(msg).totalLength);


    private static (int totalLength, int contentLength, int sourceAddressLength, int destinationAddressLength) GetDataLength(PingMsg msg)
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
