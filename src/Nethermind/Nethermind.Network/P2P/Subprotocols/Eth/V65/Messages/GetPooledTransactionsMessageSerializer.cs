// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class GetPooledTransactionsMessageSerializer : HashesMessageSerializer<GetPooledTransactionsMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<GetPooledTransactionsMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(GetPooledTransactionsMessage.Hashes));

        public override GetPooledTransactionsMessage Deserialize(IByteBuffer byteBuffer)
        {
            ArrayPoolList<Hash256>? hashes = DeserializeHashesArrayPool(byteBuffer, RlpLimit);
            return new GetPooledTransactionsMessage(hashes);
        }
    }
}
