// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Discovery.Discv5.Serializers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5;

internal static class MessageCodec
{
    private static readonly PingMsgSerializer PingSerializer = new();
    private static readonly PongMsgSerializer PongSerializer = new();
    private static readonly FindNodeMsgSerializer FindNodeSerializer = new();
    private static readonly NodesMsgSerializer NodesSerializer = new();
    private static readonly TalkReqMsgSerializer TalkReqSerializer = new();
    private static readonly TalkRespMsgSerializer TalkRespSerializer = new();

    public static ArrayPoolSpan<byte> Encode(Discv5Message message)
    {
        int contentLength = GetContentLength(message);
        ArrayPoolSpan<byte> result = new(Rlp.LengthOfSequence(contentLength) + 1);
        try
        {
            Span<byte> resultSpan = result;
            int position = 0;
            resultSpan[position++] = (byte)message.MessageType;
            position = Rlp.StartSequence(resultSpan, position, contentLength);
            EncodeContent(resultSpan, ref position, message);
        }
        catch
        {
            result.Dispose();
            throw;
        }

        return result;
    }

    public static Discv5Message Decode(ReadOnlySpan<byte> message)
    {
        if (NeedsOwnedMessage(message))
        {
            throw new RlpException("discv5 TALK messages require owned message memory. Use DecodeOwned or DecodeCopied.");
        }

        return Decode(message, default, null);
    }

    public static Discv5Message DecodeOwned(ReadOnlyMemory<byte> message, ArrayPoolSpan<byte> owner)
        => Decode(message.Span, message, owner);

    public static Discv5Message DecodeCopied(ReadOnlySpan<byte> message)
    {
        ArrayPoolSpan<byte> owner = new(message.Length);
        try
        {
            message.CopyTo(owner);
            return DecodeOwned(owner.AsReadOnlyMemory(), owner);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

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
            Rlp.ValueDecoderContext ctx = new(message[1..]);
            int checkPosition = ctx.ReadSequenceLength() + ctx.Position;

            RequestId requestId = MsgSerializerBase.DecodeRequestId(ref ctx);
            decoded = messageType switch
            {
                MessageType.Ping => PingSerializer.Deserialize(requestId, ref ctx, owner),
                MessageType.Pong => PongSerializer.Deserialize(requestId, ref ctx, owner),
                MessageType.FindNode => FindNodeSerializer.Deserialize(requestId, ref ctx, owner),
                MessageType.Nodes => NodesSerializer.Deserialize(requestId, ref ctx, owner),
                MessageType.TalkReq => TalkReqSerializer.Deserialize(requestId, ref ctx, ownedMessage, owner),
                MessageType.TalkResp => TalkRespSerializer.Deserialize(requestId, ref ctx, ownedMessage, owner),
                _ => throw new RlpException($"Unsupported discv5 message type {(byte)messageType}.")
            };

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

    private static void DisposeOwner(ArrayPoolSpan<byte>? owner)
    {
        if (owner is { } ownerValue)
        {
            ownerValue.Dispose();
        }
    }

    private static bool NeedsOwnedMessage(ReadOnlySpan<byte> message)
        => !message.IsEmpty && (MessageType)message[0] is MessageType.TalkReq or MessageType.TalkResp;

    private static int GetContentLength(Discv5Message message) => message switch
    {
        PingMsg ping => PingSerializer.GetContentLength(ping),
        PongMsg pong => PongSerializer.GetContentLength(pong),
        FindNodeMsg findNode => FindNodeSerializer.GetContentLength(findNode),
        NodesMsg nodes => NodesSerializer.GetContentLength(nodes),
        TalkReqMsg talkReq => TalkReqSerializer.GetContentLength(talkReq),
        TalkRespMsg talkResp => TalkRespSerializer.GetContentLength(talkResp),
        _ => throw new RlpException($"Unsupported discv5 message {message.GetType().Name}.")
    };

    private static void EncodeContent(Span<byte> buffer, ref int position, Discv5Message message)
    {
        switch (message)
        {
            case PingMsg ping:
                PingSerializer.Serialize(buffer, ref position, ping);
                break;
            case PongMsg pong:
                PongSerializer.Serialize(buffer, ref position, pong);
                break;
            case FindNodeMsg findNode:
                FindNodeSerializer.Serialize(buffer, ref position, findNode);
                break;
            case NodesMsg nodes:
                NodesSerializer.Serialize(buffer, ref position, nodes);
                break;
            case TalkReqMsg talkReq:
                TalkReqSerializer.Serialize(buffer, ref position, talkReq);
                break;
            case TalkRespMsg talkResp:
                TalkRespSerializer.Serialize(buffer, ref position, talkResp);
                break;
            default:
                throw new RlpException($"Unsupported discv5 message {message.GetType().Name}.");
        }
    }
}
