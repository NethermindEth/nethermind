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
        Rlp data = message switch
        {
            Discv5Ping ping => Rlp.Encode(Rlp.Encode(ping.RequestId), Rlp.Encode(ping.EnrSequence)),
            Discv5Pong pong => Rlp.Encode(
                Rlp.Encode(pong.RequestId),
                Rlp.Encode(pong.EnrSequence),
                Rlp.Encode(pong.RecipientIp.GetAddressBytes()),
                Rlp.Encode(pong.RecipientPort)),
            Discv5FindNode findNode => Rlp.Encode(Rlp.Encode(findNode.RequestId), Rlp.Encode(findNode.Distances)),
            Discv5Nodes nodes => Rlp.Encode(
                Rlp.Encode(nodes.RequestId),
                Rlp.Encode(nodes.Total),
                EncodeNodeRecords(nodes.Records)),
            Discv5TalkReq talkReq => Rlp.Encode(
                Rlp.Encode(talkReq.RequestId),
                Rlp.Encode(talkReq.Protocol),
                Rlp.Encode(talkReq.Request)),
            Discv5TalkResp talkResp => Rlp.Encode(Rlp.Encode(talkResp.RequestId), Rlp.Encode(talkResp.Response)),
            _ => throw new RlpException($"Unsupported discv5 message {message.GetType().Name}.")
        };

        byte[] result = new byte[data.Length + 1];
        result[0] = (byte)message.MessageType;
        data.Bytes.CopyTo(result.AsSpan(1));
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

    private static Rlp EncodeNodeRecords(NodeRecord[] records)
    {
        Rlp[] encodedRecords = new Rlp[records.Length];
        for (int i = 0; i < records.Length; i++)
        {
            encodedRecords[i] = new Rlp(records[i].ToRlpBytes());
        }

        return Rlp.Encode(encodedRecords);
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
        int validRecords = 0;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> record = ctx.PeekNextItem();
            try
            {
                records[validRecords++] = NodeRecord.FromBytes(record);
            }
            catch (Exception)
            {
            }

            ctx.SkipItem();
        }

        ctx.Check(checkPosition);
        if (validRecords != records.Length)
        {
            Array.Resize(ref records, validRecords);
        }

        return new Discv5Nodes(requestId, total, records);
    }
}
