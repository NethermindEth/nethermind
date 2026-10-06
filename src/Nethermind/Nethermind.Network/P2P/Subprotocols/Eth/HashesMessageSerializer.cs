// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public abstract class HashesMessageSerializer<T> : IZeroMessageSerializer<T> where T : HashesMessage
    {
        protected Hash256[] DeserializeHashes(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            Hash256[] result = DeserializeHashes(ref ctx);
            consumed = ctx.Position;
            return result;
        }

        protected static Hash256[] DeserializeHashes(ref RlpReader ctx, RlpLimit? limit = null) =>
            ctx.DecodeNonNullArray(static (ref RlpReader c) => c.DecodeKeccak(), limit: limit);

        protected ArrayPoolList<Hash256> DeserializeHashesArrayPool(ReadOnlySpan<byte> data, out int consumed, RlpLimit? limit = null)
        {
            RlpReader ctx = new(data);
            ArrayPoolList<Hash256> result = DeserializeHashesArrayPool(ref ctx, limit);
            consumed = ctx.Position;
            return result;
        }

        protected static ArrayPoolList<Hash256> DeserializeHashesArrayPool(ref RlpReader ctx, RlpLimit? limit = null) => ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) => c.DecodeKeccak(), limit: limit);

        public void Serialize(Span<byte> buffer, T message)
        {
            GetLength(message, out int contentLength);
            RlpWriter writer = new(buffer);

            writer.StartSequence(contentLength);
            ReadOnlySpan<Hash256> hashes = message.Hashes.AsSpan();
            for (int i = 0; i < hashes.Length; i++)
            {
                writer.Encode(hashes[i]);
            }
        }

        public abstract T Deserialize(ReadOnlySpan<byte> data, out int consumed);
        public int GetLength(T message, out int contentLength)
        {
            contentLength = 0;
            ReadOnlySpan<Hash256> hashes = message.Hashes.AsSpan();
            for (int i = 0; i < hashes.Length; i++)
            {
                contentLength += Rlp.LengthOf(hashes[i]);
            }

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
