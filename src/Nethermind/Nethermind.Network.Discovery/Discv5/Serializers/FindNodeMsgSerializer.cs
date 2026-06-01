// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv5.Serializers;

internal sealed class FindNodeMsgSerializer : MsgSerializerBase
{
    public int GetContentLength(FindNodeMsg msg)
        => GetRequestIdLength(msg.RequestId) + GetDistancesLength(msg.Distances);

    public void Serialize(Span<byte> buffer, ref int position, FindNodeMsg msg)
    {
        EncodeRequestId(buffer, ref position, msg.RequestId);
        EncodeDistances(buffer, ref position, msg.Distances);
    }

    public FindNodeMsg Deserialize(RequestId requestId, ref Rlp.ValueDecoderContext ctx, ArrayPoolSpan<byte>? owner)
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

    private static void EncodeDistances(Span<byte> buffer, ref int position, Distances distances)
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

    private static Distances DecodeDistances(ref Rlp.ValueDecoderContext ctx)
    {
        int checkPosition = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(checkPosition);
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
