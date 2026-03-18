// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;

public class GetReceiptsMessageSerializer70 : Eth66SerializerBase<GetReceiptsMessage70>
{
    private static readonly RlpLimit RlpLimit =
        RlpLimit.For<GetReceiptsMessage70>(NethermindSyncLimits.MaxHashesFetch, nameof(GetReceiptsMessage70.Hashes));

    protected override void SerializeInternal(IByteBuffer byteBuffer, GetReceiptsMessage70 message)
    {
        NettyRlpStream stream = new(byteBuffer);
        stream.Encode(message.FirstBlockReceiptIndex);
        int hashesContentLength = GetHashesContentLength(message.Hashes);
        stream.StartSequence(hashesContentLength);

        foreach (Hash256 hash in message.Hashes.AsSpan())
        {
            stream.Encode(hash);
        }
    }

    protected override GetReceiptsMessage70 DeserializeInternal(ref Rlp.ValueDecoderContext ctx, long requestId)
    {
        long firstIndex = ctx.DecodeLong();

        if (firstIndex < 0)
        {
            throw new RlpException("Negative firstBlockReceiptIndex is invalid");
        }

        ArrayPoolList<Hash256> hashes =
            ctx.DecodeArrayPoolList(static (ref Rlp.ValueDecoderContext nestedContext) => nestedContext.DecodeKeccak(), limit: RlpLimit);

        return new GetReceiptsMessage70(requestId, firstIndex, hashes);
    }

    protected override int GetLengthInternal(GetReceiptsMessage70 message)
    {
        return Rlp.LengthOf(message.FirstBlockReceiptIndex) + Rlp.LengthOfSequence(GetHashesContentLength(message.Hashes));
    }

    private static int GetHashesContentLength(IOwnedReadOnlyList<Hash256> hashes)
    {
        int contentLength = 0;
        for (int i = 0; i < hashes.Count; i++)
        {
            contentLength += Rlp.LengthOf(hashes[i]);
        }

        return contentLength;
    }
}
