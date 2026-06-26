// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

public class GetBlockAccessListsMessageSerializer : Eth66SerializerBase<GetBlockAccessListsMessage>
{
    private static readonly RlpLimit RlpLimit = RlpLimit.For<GetBlockAccessListsMessage>(
        GethSyncLimits.MaxBodyFetch, nameof(GetBlockAccessListsMessage.Hashes));

    protected override void SerializeInternal(IByteBuffer byteBuffer, GetBlockAccessListsMessage message)
    {
        ByteBufferRlpWriter writer = new(byteBuffer);
        int hashesContentLength = GetHashesContentLength(message.Hashes);
        writer.StartSequence(hashesContentLength);

        foreach (Hash256 hash in message.Hashes.AsSpan())
        {
            writer.Encode(hash);
        }
    }

    protected override GetBlockAccessListsMessage DeserializeInternal(ref RlpReader ctx, long requestId)
    {
        ArrayPoolList<Hash256> hashes =
            ctx.DecodeArrayPoolList(static (ref RlpReader nestedContext) => nestedContext.DecodeKeccakNonNull(), limit: RlpLimit);

        return new GetBlockAccessListsMessage(requestId, hashes);
    }

    protected override int GetLengthInternal(GetBlockAccessListsMessage message)
        => Rlp.LengthOfSequence(GetHashesContentLength(message.Hashes));

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
