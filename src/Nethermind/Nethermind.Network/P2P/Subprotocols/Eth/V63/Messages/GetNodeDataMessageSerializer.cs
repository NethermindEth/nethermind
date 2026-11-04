// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class GetNodeDataMessageSerializer : HashesMessageSerializer<GetNodeDataMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<GetNodeDataMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(GetNodeDataMessage.Hashes));

        public override GetNodeDataMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            return ctx.CheckEnd(new GetNodeDataMessage(DeserializeHashesArrayPool(ref ctx, RlpLimit)));
        }
    }
}
