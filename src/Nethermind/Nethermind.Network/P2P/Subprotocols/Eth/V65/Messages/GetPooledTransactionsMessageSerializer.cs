// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class GetPooledTransactionsMessageSerializer : GetPooledTransactionsMessageSerializer<GetPooledTransactionsMessage>;

    public class GetPooledTransactionsMessageSerializer<T> : IMessageSerializer<T>
        where T : GetPooledTransactionsMessage, INew<IOwnedReadOnlyList<ValueHash256>, T>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<T>(NethermindSyncLimits.MaxHashesFetch, nameof(GetPooledTransactionsMessage.Hashes));

        public int Serialize(Span<byte> destination, T message)
        {
            RlpWriter writer = RlpWriter.ContentWriter(destination);
            RlpSequence sequence = writer.StartSequence(GetContentLength(message));
            foreach (ref readonly ValueHash256 hash in message.Hashes.AsSpan()) writer.Encode(in hash);
            writer.EndSequence(sequence);
            return writer.Length;
        }

        public int GetLength(T message) => Rlp.LengthOfSequence(GetContentLength(message));

        private static int GetContentLength(T message) => checked(message.Hashes.Count * Rlp.LengthOfKeccakRlp);

        public T Deserialize(ReadOnlySpan<byte> data)
        {
            RlpReader reader = new(data);
            return reader.CheckEnd(T.New(reader.DecodeNonNullArrayPoolList(static (ref RlpReader r) => r.DecodeValueKeccakNonNull(), limit: RlpLimit)));
        }
    }
}
