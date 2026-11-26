// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal sealed class FindNodeMsgSerializer() : MsgSerializerBase<FindNodeMsg>(MessageType.FindNode)
{
    protected override int GetContentLengthCore(FindNodeMsg msg)
        => GetDistancesLength(msg.Distances);

    protected override void SerializeCore(ref RlpWriter writer, FindNodeMsg msg) => EncodeDistances(ref writer, msg.Distances);

    protected override FindNodeMsg DeserializeCore(in RequestId requestId, ref RlpReader ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner)
        => new(requestId, DecodeDistances(ref ctx), owner);

    private static int GetDistancesLength(Distances distances)
    {
        int contentLength = 0;
        for (int i = 0; i < distances.Count; i++)
        {
            contentLength += Rlp.LengthOf(distances[i]);
        }

        return Rlp.LengthOfSequence(contentLength);
    }

    private static void EncodeDistances(ref RlpWriter writer, Distances distances)
    {
        int contentLength = 0;
        for (int i = 0; i < distances.Count; i++)
        {
            contentLength += Rlp.LengthOf(distances[i]);
        }

        RlpSequence sequence = writer.StartSequence(contentLength);
        for (int i = 0; i < distances.Count; i++)
        {
            Encode(ref writer, distances[i]);
        }

        writer.EndSequence(sequence);
    }

    private static Distances DecodeDistances(ref RlpReader reader)
    {
        RlpReader ctx = reader.EnterSequence(out int itemLength);
        int count = ctx.CountItems();
        if (count > Distances.MaxCount)
        {
            throw new RlpReaderException($"discv5 FINDNODE distance count {count} exceeds {Distances.MaxCount}.");
        }

        Distances distances = new(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                distances.Set(i, ctx.DecodePositiveInt());
            }

            reader.ExitSequence(ctx, itemLength);
            return distances;
        }
        catch
        {
            distances.Dispose();
            throw;
        }
    }
}
