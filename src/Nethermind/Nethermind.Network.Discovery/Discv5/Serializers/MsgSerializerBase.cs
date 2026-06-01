// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal abstract class MsgSerializerBase
{
    internal static RequestId DecodeRequestId(ref Rlp.ValueDecoderContext ctx)
    {
        ReadOnlySpan<byte> requestId = ctx.DecodeByteArraySpan();
        if (requestId.Length > RequestId.MaxLength)
        {
            throw new RlpException($"discv5 request-id length {requestId.Length} exceeds {RequestId.MaxLength}.");
        }

        return RequestId.From(requestId);
    }

    protected static int GetRequestIdLength(RequestId requestId)
    {
        Span<byte> bytes = stackalloc byte[RequestId.MaxLength];
        requestId.CopyTo(bytes);
        return Rlp.LengthOf(bytes[..requestId.Length]);
    }

    protected static void EncodeRequestId(Span<byte> buffer, ref int position, RequestId requestId)
    {
        Span<byte> bytes = stackalloc byte[RequestId.MaxLength];
        requestId.CopyTo(bytes);
        position = Rlp.Encode(buffer, position, bytes[..requestId.Length]);
    }

    protected static ReadOnlyMemory<byte> DecodeByteMemory(ref Rlp.ValueDecoderContext ctx, ReadOnlyMemory<byte> ownedMessage)
    {
        ReadOnlySpan<byte> value = ctx.DecodeByteArraySpan();
        if (ownedMessage.IsEmpty)
        {
            throw new RlpException("discv5 byte fields require owned message memory.");
        }

        return ownedMessage.Slice(1 + ctx.Position - value.Length, value.Length);
    }

    protected static void Encode(Span<byte> buffer, ref int position, ulong value)
    {
        int length = Rlp.LengthOf(value);
        Rlp.Encode(value, buffer.Slice(position, length));
        position += length;
    }

    protected static void Encode(Span<byte> buffer, ref int position, int value)
    {
        int length = Rlp.LengthOf(value);
        Rlp.Encode((long)value, buffer.Slice(position, length));
        position += length;
    }
}
