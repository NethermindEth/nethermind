// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages
{
    public class GetNodeDataMessageSerializer : HashesMessageSerializer<GetNodeDataMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<GetNodeDataMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(GetNodeDataMessage.Hashes));

        public override GetNodeDataMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            ArrayPoolList<Hash256> keys = DeserializeHashesArrayPool(data, out consumed, RlpLimit);
            return new GetNodeDataMessage(keys);
        }
    }
}
