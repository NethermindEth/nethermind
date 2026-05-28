// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5;

internal static class Discv5MessageCodec
{
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
        => Decode(message, default, null);

    public static Discv5Message Decode(ReadOnlyMemory<byte> message, IDisposable owner)
        => Decode(message.Span, message, owner);

    private static Discv5Message Decode(ReadOnlySpan<byte> message, ReadOnlyMemory<byte> ownedMessage, IDisposable? owner)
    {
        if (message.IsEmpty)
        {
            owner?.Dispose();
            throw new RlpException("Empty discv5 message.");
        }

        Discv5Message? decoded = null;
        try
        {
            Discv5MessageType messageType = (Discv5MessageType)message[0];
            Rlp.ValueDecoderContext ctx = new(message[1..]);
            int checkPosition = ctx.ReadSequenceLength() + ctx.Position;

            Discv5RequestId requestId = DecodeRequestId(ref ctx);
            decoded = messageType switch
            {
                Discv5MessageType.Ping => new Discv5Ping(requestId, ctx.DecodeULong(), owner),
                Discv5MessageType.Pong => DecodePong(requestId, ref ctx, owner),
                Discv5MessageType.FindNode => new Discv5FindNode(requestId, DecodeDistances(ref ctx), owner),
                Discv5MessageType.Nodes => DecodeNodes(requestId, ref ctx, owner),
                Discv5MessageType.TalkReq => new Discv5TalkReq(
                    requestId,
                    DecodeByteMemory(ref ctx, ownedMessage),
                    DecodeByteMemory(ref ctx, ownedMessage),
                    owner),
                Discv5MessageType.TalkResp => new Discv5TalkResp(requestId, DecodeByteMemory(ref ctx, ownedMessage), owner),
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
                owner?.Dispose();
            }

            throw;
        }
    }

    private static int GetContentLength(Discv5Message message) => message switch
    {
        Discv5Ping ping => GetRequestIdLength(ping.RequestId) + Rlp.LengthOf(ping.EnrSequence),
        Discv5Pong pong => GetRequestIdLength(pong.RequestId) +
            Rlp.LengthOf(pong.EnrSequence) +
            GetAddressRlpLength(pong.RecipientIp) +
            Rlp.LengthOf(pong.RecipientPort),
        Discv5FindNode findNode => GetRequestIdLength(findNode.RequestId) + GetDistancesLength(findNode.Distances),
        Discv5Nodes nodes => GetRequestIdLength(nodes.RequestId) + Rlp.LengthOf(nodes.Total) + GetNodeRecordsLength(nodes.Records),
        Discv5TalkReq talkReq => GetRequestIdLength(talkReq.RequestId) + Rlp.LengthOf(talkReq.Protocol.Span) + Rlp.LengthOf(talkReq.Request.Span),
        Discv5TalkResp talkResp => GetRequestIdLength(talkResp.RequestId) + Rlp.LengthOf(talkResp.Response.Span),
        _ => throw new RlpException($"Unsupported discv5 message {message.GetType().Name}.")
    };

    private static void EncodeContent(Span<byte> buffer, ref int position, Discv5Message message)
    {
        switch (message)
        {
            case Discv5Ping ping:
                EncodeRequestId(buffer, ref position, ping.RequestId);
                Encode(buffer, ref position, ping.EnrSequence);
                break;
            case Discv5Pong pong:
                EncodeRequestId(buffer, ref position, pong.RequestId);
                Encode(buffer, ref position, pong.EnrSequence);
                EncodeAddress(buffer, ref position, pong.RecipientIp);
                Encode(buffer, ref position, pong.RecipientPort);
                break;
            case Discv5FindNode findNode:
                EncodeRequestId(buffer, ref position, findNode.RequestId);
                EncodeDistances(buffer, ref position, findNode.Distances);
                break;
            case Discv5Nodes nodes:
                EncodeRequestId(buffer, ref position, nodes.RequestId);
                Encode(buffer, ref position, nodes.Total);
                EncodeNodeRecords(buffer, ref position, nodes.Records);
                break;
            case Discv5TalkReq talkReq:
                EncodeRequestId(buffer, ref position, talkReq.RequestId);
                position = Rlp.Encode(buffer, position, talkReq.Protocol.Span);
                position = Rlp.Encode(buffer, position, talkReq.Request.Span);
                break;
            case Discv5TalkResp talkResp:
                EncodeRequestId(buffer, ref position, talkResp.RequestId);
                position = Rlp.Encode(buffer, position, talkResp.Response.Span);
                break;
            default:
                throw new RlpException($"Unsupported discv5 message {message.GetType().Name}.");
        }
    }

    private static int GetRequestIdLength(Discv5RequestId requestId)
    {
        Span<byte> bytes = stackalloc byte[Discv5RequestId.MaxLength];
        requestId.CopyTo(bytes);
        return Rlp.LengthOf(bytes[..requestId.Length]);
    }

    private static void EncodeRequestId(Span<byte> buffer, ref int position, Discv5RequestId requestId)
    {
        Span<byte> bytes = stackalloc byte[Discv5RequestId.MaxLength];
        requestId.CopyTo(bytes);
        position = Rlp.Encode(buffer, position, bytes[..requestId.Length]);
    }

    private static int GetDistancesLength(Discv5Distances distances)
    {
        int contentLength = 0;
        for (int i = 0; i < distances.Count; i++)
        {
            contentLength += Rlp.LengthOf(distances[i]);
        }

        return Rlp.LengthOfSequence(contentLength);
    }

    private static void EncodeDistances(Span<byte> buffer, ref int position, Discv5Distances distances)
    {
        int contentLength = 0;
        for (int i = 0; i < distances.Count; i++)
        {
            contentLength += Rlp.LengthOf(distances[i]);
        }

        position = Rlp.StartSequence(buffer, position, contentLength);
        for (int i = 0; i < distances.Count; i++)
        {
            Encode(buffer, ref position, distances[i]);
        }
    }

    private static int GetNodeRecordsLength(IReadOnlyList<NodeRecord> records)
    {
        int contentLength = 0;
        for (int i = 0; i < records.Count; i++)
        {
            contentLength += records[i].GetRlpLengthWithSignature();
        }

        return Rlp.LengthOfSequence(contentLength);
    }

    private static void EncodeNodeRecords(Span<byte> buffer, ref int position, IReadOnlyList<NodeRecord> records)
    {
        int contentLength = 0;
        for (int i = 0; i < records.Count; i++)
        {
            contentLength += records[i].GetRlpLengthWithSignature();
        }

        position = Rlp.StartSequence(buffer, position, contentLength);
        for (int i = 0; i < records.Count; i++)
        {
            records[i].Encode(buffer, ref position);
        }
    }

    private static int GetAddressRlpLength(IPAddress ip)
    {
        if (ip.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return Rlp.LengthOfByteString(4, 0);
        }

        if (ip.AddressFamily is System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return Rlp.LengthOfByteString(16, 0);
        }

        return Rlp.LengthOf(ip.GetAddressBytes());
    }

    private static void EncodeAddress(Span<byte> buffer, ref int position, IPAddress ip)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (ip.TryWriteBytes(bytes, out int bytesWritten))
        {
            position = Rlp.Encode(buffer, position, bytes[..bytesWritten]);
            return;
        }

        position = Rlp.Encode(buffer, position, ip.GetAddressBytes());
    }

    private static void Encode(Span<byte> buffer, ref int position, ulong value)
        => position += Rlp.Encode(value, buffer[position..]).Length;

    private static void Encode(Span<byte> buffer, ref int position, int value)
        => position += Rlp.Encode((long)value, buffer[position..]).Length;

    private static Discv5RequestId DecodeRequestId(ref Rlp.ValueDecoderContext ctx)
    {
        ReadOnlySpan<byte> requestId = ctx.DecodeByteArraySpan();
        if (requestId.Length > Discv5RequestId.MaxLength)
        {
            throw new RlpException($"discv5 request-id length {requestId.Length} exceeds {Discv5RequestId.MaxLength}.");
        }

        return Discv5RequestId.From(requestId);
    }

    private static ReadOnlyMemory<byte> DecodeByteMemory(ref Rlp.ValueDecoderContext ctx, ReadOnlyMemory<byte> ownedMessage)
    {
        ReadOnlySpan<byte> value = ctx.DecodeByteArraySpan();
        if (ownedMessage.IsEmpty)
        {
            return value.ToArray();
        }

        return ownedMessage.Slice(1 + ctx.Position - value.Length, value.Length);
    }

    private static Discv5Pong DecodePong(Discv5RequestId requestId, ref Rlp.ValueDecoderContext ctx, IDisposable? owner)
    {
        ulong enrSequence = ctx.DecodeULong();
        IPAddress recipientIp = new(ctx.DecodeByteArraySpan());
        int recipientPort = ctx.DecodePositiveInt();
        return new Discv5Pong(requestId, enrSequence, recipientIp, recipientPort, owner);
    }

    private static Discv5Distances DecodeDistances(ref Rlp.ValueDecoderContext ctx)
    {
        int checkPosition = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(checkPosition);
        Discv5Distances distances = new(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                distances.Set(i, ctx.DecodePositiveInt());
            }

            ctx.Check(checkPosition);
            return distances;
        }
        catch
        {
            distances.Dispose();
            throw;
        }
    }

    private static Discv5Nodes DecodeNodes(Discv5RequestId requestId, ref Rlp.ValueDecoderContext ctx, IDisposable? owner)
    {
        int total = ctx.DecodePositiveInt();
        int checkPosition = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(checkPosition);
        NodeRecord[] records = new NodeRecord[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> record = ctx.PeekNextItem();
            records[i] = NodeRecord.FromBytes(record);
            ctx.SkipItem();
        }

        ctx.Check(checkPosition);
        return new Discv5Nodes(requestId, total, records, owner);
    }
}
