// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class GetReceiptsMessageSerializer : HashesMessageSerializer<GetReceiptsMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<GetReceiptsMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(GetReceiptsMessage.Hashes));

        public override GetReceiptsMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            return ctx.CheckEnd(new GetReceiptsMessage(DeserializeHashesArrayPool(ref ctx, RlpLimit)));
        }
    }
}
