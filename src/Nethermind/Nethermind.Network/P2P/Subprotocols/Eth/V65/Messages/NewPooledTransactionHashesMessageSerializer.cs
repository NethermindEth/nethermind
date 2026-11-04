// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class NewPooledTransactionHashesMessageSerializer
        : HashesMessageSerializer<NewPooledTransactionHashesMessage>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage>(NethermindSyncLimits.MaxHashesFetch, nameof(NewPooledTransactionHashesMessage.Hashes));

        public override NewPooledTransactionHashesMessage Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader ctx = new(data);
            return ctx.CheckEnd(new NewPooledTransactionHashesMessage(DeserializeHashesArrayPool(ref ctx, RlpLimit)));
        }
    }
}
