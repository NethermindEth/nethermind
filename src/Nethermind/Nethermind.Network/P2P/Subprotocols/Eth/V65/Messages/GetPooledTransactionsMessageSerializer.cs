// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class GetPooledTransactionsMessageSerializer : GetPooledTransactionsMessageSerializer<GetPooledTransactionsMessage>;

    public class GetPooledTransactionsMessageSerializer<T> : IZeroMessageSerializer<T>
        where T : GetPooledTransactionsMessage, INew<IOwnedReadOnlyList<ValueHash256>, T>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<T>(NethermindSyncLimits.MaxHashesFetch, nameof(GetPooledTransactionsMessage.Hashes));

        public void Serialize(Span<byte> buffer, T message)
        {
            GetLength(message, out int contentLength);
            RlpWriter writer = new(buffer);
            writer.StartSequence(contentLength);
            foreach (ref readonly ValueHash256 hash in message.Hashes.AsSpan()) writer.Encode(in hash);
        }

        public int GetLength(T message, out int contentLength)
        {
            contentLength = checked(message.Hashes.Count * Rlp.LengthOfKeccakRlp);
            return Rlp.LengthOfSequence(contentLength);
        }

        public T Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader reader = new(data);
            T result = T.New(reader.DecodeNonNullArrayPoolList(static (ref RlpReader r) => r.DecodeValueKeccakNonNull(), limit: RlpLimit));
            consumed = reader.Position;
            return result;
        }
    }
}
