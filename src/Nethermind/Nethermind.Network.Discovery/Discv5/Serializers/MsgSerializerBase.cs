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

    void Serialize<TWriter>(ref TWriter writer, Discv5Message msg)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;

    Discv5Message Deserialize(ref RlpReader ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner);
}

internal abstract class MsgSerializerBase<TMessage>(MessageType messageType, bool requiresOwnedMemory = false) : IMsgSerializer
    where TMessage : Discv5Message
{
    public MessageType MessageType { get; } = messageType;

    public bool RequiresOwnedMemory { get; } = requiresOwnedMemory;

    public int GetContentLength(TMessage msg)
        => msg.RequestId.GetRlpLength() + GetContentLengthCore(msg);

    public void Serialize<TWriter>(ref TWriter writer, TMessage msg)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        RequestId requestId = msg.RequestId;
        EncodeRequestId(ref writer, in requestId);
        SerializeCore(ref writer, msg);
    }

    public TMessage Deserialize(ref RlpReader ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner)
    {
        RequestId requestId = DecodeRequestId(ref ctx);
        return DeserializeCore(in requestId, ref ctx, ownedMessage, owner);
    }

    int IMsgSerializer.GetContentLength(Discv5Message msg) => GetContentLength((TMessage)msg);

    void IMsgSerializer.Serialize<TWriter>(ref TWriter writer, Discv5Message msg) => Serialize(ref writer, (TMessage)msg);

    Discv5Message IMsgSerializer.Deserialize(ref RlpReader ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner)
        => Deserialize(ref ctx, ownedMessage, owner);

    protected abstract int GetContentLengthCore(TMessage msg);

    protected abstract void SerializeCore<TWriter>(ref TWriter writer, TMessage msg)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;

    protected abstract TMessage DeserializeCore(in RequestId requestId, ref RlpReader ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner);

    protected static ReadOnlyMemory<byte> DecodeByteMemory(ref RlpReader ctx, ReadOnlyMemory<byte> ownedMessage)
    {
        ReadOnlySpan<byte> value = ctx.DecodeByteArraySpan();
        if (ownedMessage.IsEmpty)
        {
            throw new RlpException("discv5 byte fields require owned message memory.");
        }

        return ownedMessage.Slice(1 + ctx.Position - value.Length, value.Length);
    }

    protected static void Encode<TWriter>(ref TWriter writer, ulong value)
        where TWriter : struct, IRlpWriteBackend, allows ref struct => writer.Encode(value);

    protected static void Encode<TWriter>(ref TWriter writer, int value)
        where TWriter : struct, IRlpWriteBackend, allows ref struct => writer.Encode(value);

    private static RequestId DecodeRequestId(ref RlpReader ctx)
    {
        ReadOnlySpan<byte> requestId = ctx.DecodeByteArraySpan();
        if (requestId.Length > RequestId.MaxLength)
        {
            throw new RlpException($"discv5 request-id length {requestId.Length} exceeds {RequestId.MaxLength}.");
        }

        return RequestId.From(requestId);
    }

    [SkipLocalsInit]
    private static void EncodeRequestId<TWriter>(ref TWriter writer, in RequestId requestId)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        Span<byte> bytes = stackalloc byte[RequestId.MaxLength];
        requestId.CopyTo(bytes);
        writer.Encode(bytes[..requestId.Length]);
    }
}
