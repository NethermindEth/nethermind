// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

public class GetBlockAccessListsMessageSerializer : Eth66SerializerBase<GetBlockAccessListsMessage>
{
    private static readonly RlpLimit RlpLimit = RlpLimit.For<GetBlockAccessListsMessage>(
        128, nameof(GetBlockAccessListsMessage.Hashes));

    protected override void SerializeInternal(IByteBuffer byteBuffer, GetBlockAccessListsMessage message)
    {
        NettyRlpStream stream = new(byteBuffer);
        int hashesContentLength = GetHashesContentLength(message.Hashes);
        stream.StartSequence(hashesContentLength);

        foreach (Hash256 hash in message.Hashes.AsSpan())
        {
            stream.Encode(hash);
        }
    }

    protected override GetBlockAccessListsMessage DeserializeInternal(ref Rlp.ValueDecoderContext ctx, long requestId)
    {
        ArrayPoolList<Hash256> hashes =
            ctx.DecodeArrayPoolList(static (ref Rlp.ValueDecoderContext nestedContext) => nestedContext.DecodeKeccak(), limit: RlpLimit);

        return new GetBlockAccessListsMessage(requestId, hashes);
    }

    protected override int GetLengthInternal(GetBlockAccessListsMessage message)
        => Rlp.LengthOfSequence(GetHashesContentLength(message.Hashes));

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
