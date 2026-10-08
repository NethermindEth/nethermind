// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public sealed class PongMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IZeroMessageSerializer<PongMsg>
{
    public void Serialize(Span<byte> buffer, PongMsg msg)
    {
        if (msg.FarAddress is null)
        {
            throw new NetworkingException($"Sending discovery message without {nameof(msg.FarAddress)} set.", NetworkExceptionType.Discovery);
        }

        (int _, int contentLength, int farAddressLength) = GetLength(msg);

        PrepareBufferForSerialization(buffer, (byte)msg.MsgType);
        RlpWriter writer = new(buffer.Slice(EnvelopeLength));
        writer.StartSequence(contentLength);
        Encode(ref writer, msg.FarAddress, farAddressLength);
        ValueHash256? pingMdc = msg.PingMdc;
        writer.Encode(in pingMdc);
        writer.Encode(msg.ExpirationTime);
        if (msg.EnrSequence.HasValue)
        {
            writer.Encode(msg.EnrSequence.Value);
        }

        AddSignatureAndMdc(buffer);
    }

    public PongMsg Deserialize(ReadOnlySpan<byte> data, out int consumed)
    {
        (PublicKey farPublicKey, _) = PrepareForDeserialization(data);

        ReadOnlySpan<byte> content = data.Slice(EnvelopeLength);
        RlpReader ctx = new(content);

        int messageEnd = ctx.ReadSequenceLength() + ctx.Position;
        int addressEnd = ctx.ReadSequenceLength() + ctx.Position;

        ctx.DecodeByteArraySpan(IpAddressRlpLimit);
        ctx.DecodeInt(); // UDP port (we ignore and take it from Netty)
        ctx.DecodeInt(); // TCP port
        ctx.Check(addressEnd);
        ReadOnlySpan<byte> token = ctx.DecodeByteArraySpan(RlpLimit.L32);
        if (token.Length != Hash256.Size)
        {
            throw new NetworkingException($"PONG ping MDC must be {Hash256.Size} bytes.", NetworkExceptionType.Validation);
        }

        long expirationTime = ctx.DecodeLong();

        ulong? enrSequence = null;
        if (ctx.Position < messageEnd && IsNextEnrSequence(ctx))
        {
            enrSequence = ctx.DecodeULong();
        }

        while (ctx.Position < messageEnd)
        {
            ctx.SkipItem();
        }

        ctx.Check(messageEnd);
        consumed = EnvelopeLength + ctx.Position;
        PongMsg msg = new(farPublicKey, expirationTime, new ValueHash256(token), enrSequence);
        return msg;
    }

    public int GetLength(PongMsg message, out int contentLength)
    {
        (int totalLength, contentLength, int _) = GetLength(message);
        return EnvelopeLength + totalLength;
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
}
