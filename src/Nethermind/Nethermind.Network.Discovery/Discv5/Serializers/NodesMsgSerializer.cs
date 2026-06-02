// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal sealed class NodesMsgSerializer : MsgSerializerBase
{
    public int GetContentLength(NodesMsg msg)
        => GetRequestIdLength(msg.RequestId) + Rlp.LengthOf(msg.Total) + GetNodeRecordsLength(msg.Records);

    public void Serialize(NettyRlpStream stream, NodesMsg msg)
    {
        EncodeRequestId(stream, msg.RequestId);
        Encode(stream, msg.Total);
        EncodeNodeRecords(stream, msg.Records);
    }

    public NodesMsg Deserialize(RequestId requestId, ref Rlp.ValueDecoderContext ctx, ArrayPoolSpan<byte>? owner)
    {
        int total = ctx.DecodePositiveInt();
        return new NodesMsg(requestId, total, DecodeNodeRecords(ref ctx), owner);
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

    private static void EncodeNodeRecords(NettyRlpStream stream, IReadOnlyList<NodeRecord> records)
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

    private static NodeRecord[] DecodeNodeRecords(ref Rlp.ValueDecoderContext ctx)
    {
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
        return records;
    }
}
