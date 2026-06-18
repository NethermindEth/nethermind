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

    public static NettyRlpStream Encode(Discv5Message message)
    {
        int contentLength = GetContentLength(message);
        NettyRlpStream stream = new(NethermindBuffers.Default.Buffer(Rlp.LengthOfSequence(contentLength) + 1));
        try
        {
            stream.WriteByte((byte)message.MessageType);
            stream.StartSequence(contentLength);
            EncodeContent(stream, message);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        return stream;
    }

    public static Discv5Message Decode(ReadOnlySpan<byte> message)
        => NeedsOwnedMessage(message)
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
            Rlp.ValueDecoderContext ctx = new(message[1..]);
            int checkPosition = ctx.ReadSequenceLength() + ctx.Position;

            decoded = messageType switch
            {
                MessageType.Ping => PingSerializer.Deserialize(ref ctx, ownedMessage, owner),
                MessageType.Pong => PongSerializer.Deserialize(ref ctx, ownedMessage, owner),
                MessageType.FindNode => FindNodeSerializer.Deserialize(ref ctx, ownedMessage, owner),
                MessageType.Nodes => NodesSerializer.Deserialize(ref ctx, ownedMessage, owner),
                MessageType.TalkReq => TalkReqSerializer.Deserialize(ref ctx, ownedMessage, owner),
                MessageType.TalkResp => TalkRespSerializer.Deserialize(ref ctx, ownedMessage, owner),
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

    private static void EncodeContent(NettyRlpStream stream, Discv5Message message)
    {
        switch (message)
        {
            case PingMsg ping:
                PingSerializer.Serialize(stream, ping);
                break;
            case PongMsg pong:
                PongSerializer.Serialize(stream, pong);
                break;
            case FindNodeMsg findNode:
                FindNodeSerializer.Serialize(stream, findNode);
                break;
            case NodesMsg nodes:
                NodesSerializer.Serialize(stream, nodes);
                break;
            case TalkReqMsg talkReq:
                TalkReqSerializer.Serialize(stream, talkReq);
                break;
            case TalkRespMsg talkResp:
                TalkRespSerializer.Serialize(stream, talkResp);
                break;
            default:
                throw new RlpException($"Unsupported discv5 message {message.GetType().Name}.");
        }
    }
}
