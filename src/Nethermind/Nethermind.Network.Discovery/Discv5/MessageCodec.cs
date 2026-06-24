// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Discovery.Discv5.Serializers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5;

internal static class MessageCodec
{
    private static readonly IMsgSerializer?[] Serializers = [null,
        new PingMsgSerializer(),
        new PongMsgSerializer(),
        new FindNodeMsgSerializer(),
        new NodesMsgSerializer(),
        new TalkReqMsgSerializer(),
        new TalkRespMsgSerializer(),
    ];

    public static ArrayPoolSpan<byte> Encode(Discv5Message message)
    {
        IMsgSerializer serializer = GetSerializer(message.MessageType);
        int contentLength = serializer.GetContentLength(message);
        ArrayPoolSpan<byte> buffer = new(Rlp.LengthOfSequence(contentLength) + 1);

        try
        {
            Span<byte> span = buffer;
            span[0] = (byte)message.MessageType;
            RlpWriter writer = new(span[1..]);
            writer.StartSequence(contentLength);
            serializer.Serialize(ref writer, message);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }

        return buffer;
    }

    public static Discv5Message Decode(ReadOnlySpan<byte> message)
        => RequiresOwnedMessage(message)
            ? throw new RlpException("discv5 TALK messages require owned message memory. Use DecodeOwned.")
            : Decode(message, default, null);

    public static Discv5Message DecodeOwned(ReadOnlyMemory<byte> message, ArrayPoolSpan<byte> owner)
        => Decode(message.Span, message, owner);

    private static Discv5Message Decode(ReadOnlySpan<byte> message, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner)
    {
        if (message.IsEmpty)
        {
            DisposeOwner(owner);
            throw new RlpException("Empty discv5 message.");
        }

        Discv5Message? decoded = null;
        try
        {
            MessageType messageType = (MessageType)message[0];
            IMsgSerializer serializer = GetSerializer(messageType);
            RlpReader ctx = new(message[1..]);
            int checkPosition = ctx.ReadSequenceLength() + ctx.Position;

            decoded = serializer.Deserialize(ref ctx, ownedMessage, owner);
            ctx.Check(checkPosition);
            ctx.CheckEnd();
            return decoded;
        }
        catch
        {
            if (decoded is not null)
            {
                decoded.Dispose();
            }
            else
            {
                DisposeOwner(owner);
            }

            throw;
        }
    }

    private static IMsgSerializer GetSerializer(MessageType messageType)
    {
        int type = (byte)messageType;
        if ((uint)type < (uint)Serializers.Length && Serializers[type] is { } serializer)
        {
            return serializer;
        }

        throw new RlpException($"Unsupported discv5 message type {(byte)messageType}.");
    }

    private static void DisposeOwner(ArrayPoolSpan<byte>? owner)
    {
        if (owner is { } ownerValue)
        {
            ownerValue.Dispose();
        }
    }

    private static bool RequiresOwnedMessage(ReadOnlySpan<byte> message)
        => !message.IsEmpty
            && message[0] < Serializers.Length
            && Serializers[message[0]] is { RequiresOwnedMemory: true };
}
