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
        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.Encode(message.FirstBlockReceiptIndex);
        int hashesContentLength = GetHashesContentLength(message.Hashes);
        writer.StartSequence(hashesContentLength);

        foreach (Hash256 hash in message.Hashes.AsSpan())
        {
            writer.Encode(hash);
        }
    }

    protected override GetReceiptsMessage70 DeserializeInternal(ref RlpReader ctx, long requestId)
    {
        long firstIndex = ctx.DecodeLong();

        if (firstIndex < 0)
        {
            throw new RlpException("Negative firstBlockReceiptIndex is invalid");
        }

        ArrayPoolList<Hash256> hashes =
            ctx.DecodeArrayPoolList(static (ref RlpReader nestedContext) => nestedContext.DecodeKeccak(), limit: RlpLimit);

        return new GetReceiptsMessage70(requestId, firstIndex, hashes);
    }

    protected override int GetLengthInternal(GetReceiptsMessage70 message) =>
        Rlp.LengthOf(message.FirstBlockReceiptIndex) + Rlp.LengthOfSequence(GetHashesContentLength(message.Hashes));

    private static int GetHashesContentLength(IOwnedReadOnlyList<Hash256> hashes)
    {
        int contentLength = 0;
        foreach (Hash256 hash in hashes.AsSpan())
        {
            contentLength += Rlp.LengthOf(hash);
        }

        return contentLength;
    }
}
