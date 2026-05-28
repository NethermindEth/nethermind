// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5;

internal static class Discv5MessageCodec
{
    private const int MaxRequestIdLength = 8;

    public static byte[] Encode(Discv5Message message)
    {
        int contentLength = GetContentLength(message);
        byte[] result = new byte[Rlp.LengthOfSequence(contentLength) + 1];
        result[0] = (byte)message.MessageType;
        RlpStream stream = new(result) { Position = 1 };
        stream.StartSequence(contentLength);
        EncodeContent(stream, message);
        return result;
    }

    public static Discv5Message Decode(ReadOnlySpan<byte> message)
    {
        if (message.IsEmpty)
        {
            throw new RlpException("Empty discv5 message.");
        }

        Discv5MessageType messageType = (Discv5MessageType)message[0];
        Rlp.ValueDecoderContext ctx = new(message[1..]);
        int checkPosition = ctx.ReadSequenceLength() + ctx.Position;

        byte[] requestId = DecodeRequestId(ref ctx);
        Discv5Message decoded = messageType switch
        {
            Discv5MessageType.Ping => new Discv5Ping(requestId, ctx.DecodeULong()),
            Discv5MessageType.Pong => DecodePong(requestId, ref ctx),
            Discv5MessageType.FindNode => new Discv5FindNode(requestId, DecodeDistances(ref ctx)),
            Discv5MessageType.Nodes => DecodeNodes(requestId, ref ctx),
            Discv5MessageType.TalkReq => new Discv5TalkReq(requestId, ctx.DecodeByteArray(), ctx.DecodeByteArray()),
            Discv5MessageType.TalkResp => new Discv5TalkResp(requestId, ctx.DecodeByteArray()),
            _ => throw new RlpException($"Unsupported discv5 message type {(byte)messageType}.")
        };

        ctx.Check(checkPosition);
        ctx.CheckEnd();
        return decoded;
    }

    private static int GetContentLength(Discv5Message message) => message switch
    {
        Discv5Ping ping => Rlp.LengthOf(ping.RequestId) + Rlp.LengthOf(ping.EnrSequence),
        Discv5Pong pong => Rlp.LengthOf(pong.RequestId) +
            Rlp.LengthOf(pong.EnrSequence) +
            GetAddressRlpLength(pong.RecipientIp) +
            Rlp.LengthOf(pong.RecipientPort),
        Discv5FindNode findNode => Rlp.LengthOf(findNode.RequestId) + GetDistancesLength(findNode.Distances),
        Discv5Nodes nodes => Rlp.LengthOf(nodes.RequestId) + Rlp.LengthOf(nodes.Total) + GetNodeRecordsLength(nodes.Records),
        Discv5TalkReq talkReq => Rlp.LengthOf(talkReq.RequestId) + Rlp.LengthOf(talkReq.Protocol) + Rlp.LengthOf(talkReq.Request),
        Discv5TalkResp talkResp => Rlp.LengthOf(talkResp.RequestId) + Rlp.LengthOf(talkResp.Response),
        _ => throw new RlpException($"Unsupported discv5 message {message.GetType().Name}.")
    };

    private static void EncodeContent(RlpStream stream, Discv5Message message)
    {
        switch (message)
        {
            case Discv5Ping ping:
                stream.Encode(ping.RequestId);
                stream.Encode(ping.EnrSequence);
                break;
            case Discv5Pong pong:
                stream.Encode(pong.RequestId);
                stream.Encode(pong.EnrSequence);
                EncodeAddress(stream, pong.RecipientIp);
                stream.Encode(pong.RecipientPort);
                break;
            case Discv5FindNode findNode:
                stream.Encode(findNode.RequestId);
                EncodeDistances(stream, findNode.Distances);
                break;
            case Discv5Nodes nodes:
                stream.Encode(nodes.RequestId);
                stream.Encode(nodes.Total);
                EncodeNodeRecords(stream, nodes.Records);
                break;
            case Discv5TalkReq talkReq:
                stream.Encode(talkReq.RequestId);
                stream.Encode(talkReq.Protocol);
                stream.Encode(talkReq.Request);
                break;
            case Discv5TalkResp talkResp:
                stream.Encode(talkResp.RequestId);
                stream.Encode(talkResp.Response);
                break;
            default:
                throw new RlpException($"Unsupported discv5 message {message.GetType().Name}.");
        }
    }

    private static int GetDistancesLength(int[] distances)
    {
        int contentLength = 0;
        for (int i = 0; i < distances.Length; i++)
        {
            contentLength += Rlp.LengthOf(distances[i]);
        }

        return Rlp.LengthOfSequence(contentLength);
    }

    private static void EncodeDistances(RlpStream stream, int[] distances)
    {
        int contentLength = 0;
        for (int i = 0; i < distances.Length; i++)
        {
            contentLength += Rlp.LengthOf(distances[i]);
        }

        stream.StartSequence(contentLength);
        for (int i = 0; i < distances.Length; i++)
        {
            stream.Encode(distances[i]);
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

    private static void EncodeNodeRecords(RlpStream stream, IReadOnlyList<NodeRecord> records)
    {
        int contentLength = 0;
        for (int i = 0; i < records.Count; i++)
        {
            contentLength += records[i].GetRlpLengthWithSignature();
        }

        stream.StartSequence(contentLength);
        for (int i = 0; i < records.Count; i++)
        {
            records[i].Encode(stream);
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

    private static void EncodeAddress(RlpStream stream, IPAddress ip)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (ip.TryWriteBytes(bytes, out int bytesWritten))
        {
            stream.Encode(bytes[..bytesWritten]);
            return;
        }

        stream.Encode(ip.GetAddressBytes());
    }

    private static byte[] DecodeRequestId(ref Rlp.ValueDecoderContext ctx)
    {
        byte[] requestId = ctx.DecodeByteArray();
        if (requestId.Length > MaxRequestIdLength)
        {
            throw new RlpException($"discv5 request-id length {requestId.Length} exceeds {MaxRequestIdLength}.");
        }

        return requestId;
    }

    private static Discv5Pong DecodePong(byte[] requestId, ref Rlp.ValueDecoderContext ctx)
    {
        ulong enrSequence = ctx.DecodeULong();
        IPAddress recipientIp = new(ctx.DecodeByteArray());
        int recipientPort = ctx.DecodePositiveInt();
        return new Discv5Pong(requestId, enrSequence, recipientIp, recipientPort);
    }

    private static int[] DecodeDistances(ref Rlp.ValueDecoderContext ctx)
    {
        int checkPosition = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(checkPosition);
        int[] distances = new int[count];
        for (int i = 0; i < count; i++)
        {
            distances[i] = ctx.DecodePositiveInt();
        }

        ctx.Check(checkPosition);
        return distances;
    }

    private static Discv5Nodes DecodeNodes(byte[] requestId, ref Rlp.ValueDecoderContext ctx)
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
        return new Discv5Nodes(requestId, total, records);
    }
}
