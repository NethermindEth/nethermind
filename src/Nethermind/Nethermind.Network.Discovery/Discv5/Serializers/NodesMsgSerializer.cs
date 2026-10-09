// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal sealed class NodesMsgSerializer() : MsgSerializerBase<NodesMsg>(MessageType.Nodes)
{
    private const int MaxNodeRecordsPerMessage = 16;

    private readonly NodeRecordSigner _signer = new(new Ecdsa());

    protected override int GetContentLengthCore(NodesMsg msg)
        => Rlp.LengthOf(msg.Total) + GetNodeRecordsLength(msg.Records);

    protected override void SerializeCore<TWriter>(ref TWriter writer, NodesMsg msg)
    {
        Encode(ref writer, msg.Total);
        EncodeNodeRecords(ref writer, msg.Records);
    }

    protected override NodesMsg DeserializeCore(in RequestId requestId, ref RlpReader ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner)
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

    private static void EncodeNodeRecords<TWriter>(ref TWriter writer, IReadOnlyList<NodeRecord> records)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int contentLength = 0;
        for (int i = 0; i < records.Count; i++)
        {
            contentLength += records[i].GetRlpLengthWithSignature();
        }

        writer.StartSequence(contentLength);
        for (int i = 0; i < records.Count; i++)
        {
            records[i].Encode(ref writer);
        }
    }

    private NodeRecord[] DecodeNodeRecords(ref RlpReader ctx)
    {
        int checkPosition = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(checkPosition);
        if (count > MaxNodeRecordsPerMessage)
        {
            throw new RlpException($"discv5 NODES record count {count} exceeds {MaxNodeRecordsPerMessage}.");
        }

        NodeRecord[] records = new NodeRecord[count];
        int recordCount = 0;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> record = ctx.PeekNextItem();
            ctx.SkipItem();
            if (TryDecodeNodeRecord(record, out NodeRecord? nodeRecord))
            {
                records[recordCount++] = nodeRecord;
            }
        }

        ctx.Check(checkPosition);
        if (recordCount != count)
        {
            Array.Resize(ref records, recordCount);
        }

        return records;
    }

    private bool TryDecodeNodeRecord(ReadOnlySpan<byte> record, [NotNullWhen(true)] out NodeRecord? nodeRecord)
    {
        if (record.IsEmpty || record[0] < 0xc0 || record.Length > 300)
        {
            nodeRecord = null;
            return false;
        }

        try
        {
            RlpReader reader = new(record);
            if (!_signer.TryDeserialize(ref reader, out nodeRecord) || reader.Position != record.Length || !_signer.Verify(nodeRecord))
            {
                nodeRecord = null;
                return false;
            }
            return true;
        }
        catch (Exception e) when (IsMalformedNodeRecordException(e))
        {
            nodeRecord = null;
            return false;
        }
    }

    private static bool IsMalformedNodeRecordException(Exception exception)
        => exception is RlpException or ArgumentException or InvalidOperationException or FormatException;
}
