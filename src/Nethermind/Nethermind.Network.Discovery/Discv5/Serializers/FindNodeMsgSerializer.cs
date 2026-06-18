// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal sealed class FindNodeMsgSerializer : MsgSerializerBase<FindNodeMsg>
{
    protected override int GetContentLengthCore(FindNodeMsg msg)
        => GetDistancesLength(msg.Distances);

    protected override void SerializeCore(NettyRlpStream stream, FindNodeMsg msg)
        => EncodeDistances(stream, msg.Distances);

    protected override FindNodeMsg DeserializeCore(in RequestId requestId, ref Rlp.ValueDecoderContext ctx, ReadOnlyMemory<byte> ownedMessage, ArrayPoolSpan<byte>? owner)
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

    private static void EncodeDistances(NettyRlpStream stream, Distances distances)
    {
        int contentLength = 0;
        for (int i = 0; i < distances.Count; i++)
        {
            contentLength += Rlp.LengthOf(distances[i]);
        }

        stream.StartSequence(contentLength);
        for (int i = 0; i < distances.Count; i++)
        {
            Encode(stream, distances[i]);
        }
    }

    private static Distances DecodeDistances(ref Rlp.ValueDecoderContext ctx)
    {
        int checkPosition = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(checkPosition);
        if (count > Distances.MaxCount)
        {
            throw new RlpException($"discv5 FINDNODE distance count {count} exceeds {Distances.MaxCount}.");
        }

        Distances distances = new(count);
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
}
