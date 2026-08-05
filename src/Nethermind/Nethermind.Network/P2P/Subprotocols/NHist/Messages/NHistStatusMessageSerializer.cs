// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.SnapServer;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class NHistStatusMessageSerializer : NHistSerializerBase<NHistStatusMessage>
{
    protected override NHistStatusMessage Deserialize(ref RlpReader ctx)
    {
        NHistStatusMessage message = new();
        ctx.ReadSequenceLength();

        message.RequestId = ctx.DecodeLong();
        message.Scopes = ctx.DecodeArray(static (ref RlpReader c) => DecodeScope(ref c), limit: NHistMessageLimits.ServedScopesRlpLimit);

        return message;
    }

    private static HistoryServingScope DecodeScope(ref RlpReader ctx)
    {
        ctx.ReadSequenceLength();
        ValueHash256 start = ctx.DecodeValueKeccak() ?? default;
        ValueHash256 end = ctx.DecodeValueKeccak() ?? default;
        ulong floor = ctx.DecodeULong();
        ulong watermark = ctx.DecodeULong();
        return new HistoryServingScope(start, end, floor, watermark);
    }

    public override void Serialize(IByteBuffer byteBuffer, NHistStatusMessage message)
    {
        ByteBufferRlpWriter writer = GetRlpWriterAndStartSequence(byteBuffer, message);

        writer.Encode(message.RequestId);

        writer.StartSequence(ScopesContentLength(message.Scopes));
        for (int i = 0; i < message.Scopes.Length; i++)
        {
            HistoryServingScope scope = message.Scopes[i];
            int scopeLength = ScopeLength(scope);
            writer.StartSequence(scopeLength);
            writer.Encode(scope.KeyRangeStart);
            writer.Encode(scope.KeyRangeEnd);
            writer.Encode(scope.FloorBlock);
            writer.Encode(scope.WatermarkBlock);
        }
    }

    public override int GetLength(NHistStatusMessage message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.RequestId);
        contentLength += Rlp.LengthOfSequence(ScopesContentLength(message.Scopes));

        return Rlp.LengthOfSequence(contentLength);
    }

    private static int ScopeLength(HistoryServingScope scope) =>
        Rlp.LengthOf((ValueHash256?)scope.KeyRangeStart) + Rlp.LengthOf((ValueHash256?)scope.KeyRangeEnd) + Rlp.LengthOf(scope.FloorBlock) + Rlp.LengthOf(scope.WatermarkBlock);

    private static int ScopesContentLength(HistoryServingScope[] scopes)
    {
        int length = 0;
        for (int i = 0; i < scopes.Length; i++)
        {
            length += Rlp.LengthOfSequence(ScopeLength(scopes[i]));
        }

        return length;
    }
}
