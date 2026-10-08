// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class GetReceiptsMessageSerializer : HashesMessageSerializer<GetReceiptsMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<GetReceiptsMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(GetReceiptsMessage.Hashes));

        public override GetReceiptsMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            GetReceiptsMessage msg = Deserialize(ref ctx);
            consumed = ctx.Position;
            return msg;
        }

        public static GetReceiptsMessage Deserialize(ref RlpReader ctx)
        {
            ArrayPoolList<Hash256> hashes = DeserializeHashesArrayPool(ref ctx, RlpLimit);
            return new GetReceiptsMessage(hashes);
        }
    }
}
