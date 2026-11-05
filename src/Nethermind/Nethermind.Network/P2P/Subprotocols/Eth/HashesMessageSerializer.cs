// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public abstract class HashesMessageSerializer<T> : IMessageSerializer<T> where T : HashesMessage
    {
        protected static ArrayPoolList<Hash256> DeserializeHashesArrayPool(ref RlpReader ctx, RlpLimit? limit = null) => ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) => c.DecodeKeccak(), limit: limit);

        public int Serialize(Span<byte> destination, T message)
        {
            RlpWriter writer = new(destination);

            writer.StartSequence(GetContentLength(message));
            ReadOnlySpan<Hash256> hashes = message.Hashes.AsSpan();
            for (int i = 0; i < hashes.Length; i++)
            {
                writer.Encode(hashes[i]);
            }

            return writer.Position;
        }

        public abstract T Deserialize(ReadOnlySpan<byte> data);

        public int GetLength(T message) => Rlp.LengthOfSequence(GetContentLength(message));

        private static int GetContentLength(T message)
        {
            int contentLength = 0;
            ReadOnlySpan<Hash256> hashes = message.Hashes.AsSpan();
            for (int i = 0; i < hashes.Length; i++)
            {
                contentLength += Rlp.LengthOf(hashes[i]);
            }

            return contentLength;
        }
    }
}
