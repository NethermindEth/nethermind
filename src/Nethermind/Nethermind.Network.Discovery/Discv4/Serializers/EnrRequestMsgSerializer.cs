// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public sealed class EnrRequestMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IZeroMessageSerializer<EnrRequestMsg>
{
    public void Serialize(Span<byte> buffer, EnrRequestMsg msg)
    {
        GetLength(msg, out int contentLength);

        PrepareBufferForSerialization(buffer, (byte)msg.MsgType);
        RlpWriter writer = new(buffer.Slice(EnvelopeLength));
        writer.StartSequence(contentLength);
        writer.Encode(msg.ExpirationTime);

        AddSignatureAndMdc(buffer);

        msg.Hash = new ValueHash256(buffer.Slice(0, Hash256.Size));
    }

    public EnrRequestMsg Deserialize(ReadOnlySpan<byte> data, out int consumed)
    {
        (PublicKey farPublicKey, ValueHash256 mdc) = PrepareForDeserialization(data);
        ReadOnlySpan<byte> content = data.Slice(EnvelopeLength);
        RlpReader ctx = new(content);

        ctx.ReadSequenceLength();
        long expirationTime = ctx.DecodeLong();

        consumed = EnvelopeLength + ctx.Position;
        EnrRequestMsg msg = new(farPublicKey, mdc, expirationTime);
        return msg;
    }

    public int GetLength(EnrRequestMsg message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.ExpirationTime);
        return EnvelopeLength + Rlp.LengthOfSequence(contentLength);
    }
}
