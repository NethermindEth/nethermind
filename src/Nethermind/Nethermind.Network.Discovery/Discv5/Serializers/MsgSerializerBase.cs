// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal interface IMsgSerializer
{
    MessageType MessageType { get; }

    bool RequiresOwnedMemory { get; }

    int GetContentLength(Discv5Message msg);

    void Serialize(NettyRlpStream stream, Discv5Message msg);

    Discv5Message Deserialize(ref Rlp.ValueDecoderContext ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner);
}

internal abstract class MsgSerializerBase<TMessage>(MessageType messageType, bool requiresOwnedMemory = false) : IMsgSerializer
    where TMessage : Discv5Message
{
    public MessageType MessageType { get; } = messageType;

    public bool RequiresOwnedMemory { get; } = requiresOwnedMemory;

    public int GetContentLength(TMessage msg)
        => msg.RequestId.GetRlpLength() + GetContentLengthCore(msg);

    public void Serialize(NettyRlpStream stream, TMessage msg)
    {
        RequestId requestId = msg.RequestId;
        EncodeRequestId(stream, in requestId);
        SerializeCore(stream, msg);
    }

    public TMessage Deserialize(ref Rlp.ValueDecoderContext ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner)
    {
        RequestId requestId = DecodeRequestId(ref ctx);
        return DeserializeCore(in requestId, ref ctx, ownedMessage, owner);
    }

    int IMsgSerializer.GetContentLength(Discv5Message msg) => GetContentLength((TMessage)msg);

    void IMsgSerializer.Serialize(NettyRlpStream stream, Discv5Message msg) => Serialize(stream, (TMessage)msg);

    Discv5Message IMsgSerializer.Deserialize(ref Rlp.ValueDecoderContext ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner)
        => Deserialize(ref ctx, ownedMessage, owner);

    protected abstract int GetContentLengthCore(TMessage msg);

    protected abstract void SerializeCore(NettyRlpStream stream, TMessage msg);

    protected abstract TMessage DeserializeCore(in RequestId requestId, ref Rlp.ValueDecoderContext ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner);

    protected static ReadOnlyMemory<byte> DecodeByteMemory(ref Rlp.ValueDecoderContext ctx, ReadOnlyMemory<byte> ownedMessage)
    {
        ReadOnlySpan<byte> value = ctx.DecodeByteArraySpan();
        if (ownedMessage.IsEmpty)
        {
            throw new RlpException("discv5 byte fields require owned message memory.");
        }

        return ownedMessage.Slice(1 + ctx.Position - value.Length, value.Length);
    }

    protected static void Encode(NettyRlpStream stream, ulong value) => stream.Encode(value);

    protected static void Encode(NettyRlpStream stream, int value) => stream.Encode(value);

    private static RequestId DecodeRequestId(ref Rlp.ValueDecoderContext ctx)
    {
        ReadOnlySpan<byte> requestId = ctx.DecodeByteArraySpan();
        if (requestId.Length > RequestId.MaxLength)
        {
            throw new RlpException($"discv5 request-id length {requestId.Length} exceeds {RequestId.MaxLength}.");
        }

        return RequestId.From(requestId);
    }

    [SkipLocalsInit]
    private static void EncodeRequestId(NettyRlpStream stream, in RequestId requestId)
    {
        Span<byte> bytes = stackalloc byte[RequestId.MaxLength];
        requestId.CopyTo(bytes);
        stream.Encode(bytes[..requestId.Length]);
    }
}
