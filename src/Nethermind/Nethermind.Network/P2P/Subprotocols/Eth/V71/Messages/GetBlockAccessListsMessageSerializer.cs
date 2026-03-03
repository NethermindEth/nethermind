// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

public class GetBlockAccessListsMessageSerializer : HashesMessageSerializer<GetBlockAccessListsMessage>
{
    private static readonly RlpLimit RlpLimit = RlpLimit.For<GetBlockAccessListsMessage>(
        128, nameof(GetBlockAccessListsMessage.Hashes));

    public static GetBlockAccessListsMessage Deserialize(ref Rlp.ValueDecoderContext ctx)
    {
        ArrayPoolList<Hash256> hashes = DeserializeHashesArrayPool(ref ctx, RlpLimit);
        return new GetBlockAccessListsMessage(hashes);
    }

    public override GetBlockAccessListsMessage Deserialize(DotNetty.Buffers.IByteBuffer byteBuffer)
        => byteBuffer.DeserializeRlp(Deserialize);
}
