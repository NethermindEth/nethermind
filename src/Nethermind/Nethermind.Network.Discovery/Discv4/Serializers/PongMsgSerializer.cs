// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public sealed class PongMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IZeroInnerMessageSerializer<PongMsg>
{
    public void Serialize(IByteBuffer byteBuffer, PongMsg msg)
    {
        if (msg.FarAddress is null)
        {
            throw new NetworkingException($"Sending discovery message without {nameof(msg.FarAddress)} set.", NetworkExceptionType.Discovery);
        }

        (int totalLength, int contentLength, int farAddressLength) = GetLength(msg);

        byteBuffer.MarkIndex();
        PrepareBufferForSerialization(byteBuffer, totalLength, (byte)msg.MsgType);
        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(contentLength);
        Encode(stream, msg.FarAddress, farAddressLength);
        ValueHash256? pingMdc = msg.PingMdc;
        stream.Encode(in pingMdc);
        stream.Encode(msg.ExpirationTime);
        if (msg.EnrSequence.HasValue)
        {
            stream.Encode(msg.EnrSequence.Value);
        }

        byteBuffer.ResetIndex();

        AddSignatureAndMdc(byteBuffer, totalLength + 1);
    }

    public PongMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey farPublicKey, _, IByteBuffer data) = PrepareForDeserialization(msgBytes);

        Rlp.ValueDecoderContext ctx = data.AsRlpContext();

        int contentLength = ctx.ReadSequenceLength();
        int checkPosition = ctx.Position + contentLength;
        ctx.ReadSequenceLength();

        ctx.DecodeByteArraySpan(IpAddressRlpLimit);
        ctx.DecodeInt(); // UDP port (we ignore and take it from Netty)
        ctx.DecodeInt(); // TCP port
        ReadOnlySpan<byte> token = ctx.DecodeByteArraySpan(RlpLimit.L32);
        if (token.Length != Hash256.Size)
        {
            throw new NetworkingException($"PONG ping MDC must be {Hash256.Size} bytes.", NetworkExceptionType.Validation);
        }

        long expirationTime = ctx.DecodeLong();
        ulong? enrSequence = null;
        if (ctx.Position < checkPosition && IsNextUnsignedInteger(ctx))
        {
            enrSequence = ctx.DecodeULong();
        }

        ctx.Position = checkPosition;
        data.SetReaderIndex(data.ReaderIndex + ctx.Position);
        PongMsg msg = new(farPublicKey, expirationTime, new ValueHash256(token), enrSequence);
        return msg;
    }

    public int GetLength(PongMsg message, out int contentLength)
    {
        (int totalLength, contentLength, int _) = GetLength(message);
        return totalLength;
    }

    private static (int totalLength, int contentLength, int farAddressLength) GetLength(PongMsg message)
    {
        if (message.FarAddress is null)
        {
            throw new NetworkingException($"Sending discovery message without {nameof(message.FarAddress)} set.",
                NetworkExceptionType.Discovery);
        }

        int farAddressLength = GetIPEndPointLength(message.FarAddress);
        int contentLength = Rlp.LengthOfSequence(farAddressLength);
        ValueHash256? pingMdc = message.PingMdc;
        contentLength += Rlp.LengthOf(in pingMdc);
        contentLength += Rlp.LengthOf(message.ExpirationTime);
        if (message.EnrSequence.HasValue)
        {
            contentLength += Rlp.LengthOf(message.EnrSequence.Value);
        }

        return (Rlp.LengthOfSequence(contentLength), contentLength, farAddressLength);
    }

    private static bool IsNextUnsignedInteger(Rlp.ValueDecoderContext ctx)
    {
        if (ctx.IsSequenceNext())
        {
            return false;
        }

        byte prefix = ctx.PeekByte();
        return prefix <= 136;
    }
}
