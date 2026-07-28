// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public abstract class HashesMessageSerializer<T> : IZeroInnerMessageSerializer<T> where T : HashesMessage
    {
        protected Hash256[] DeserializeHashes(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(static (ref RlpReader ctx) => DeserializeHashes(ref ctx));

        protected static Hash256[] DeserializeHashes(ref RlpReader ctx, RlpLimit? limit = null) =>
            ctx.DecodeArray(static (ref RlpReader c) => c.DecodeKeccak(), limit: limit);

        protected ArrayPoolList<Hash256> DeserializeHashesArrayPool(IByteBuffer byteBuffer, RlpLimit? limit = null) =>
            byteBuffer.DeserializeRlp((ref RlpReader ctx) => DeserializeHashesArrayPool(ref ctx, limit));

        protected static ArrayPoolList<Hash256> DeserializeHashesArrayPool(ref RlpReader ctx, RlpLimit? limit = null) => ctx.DecodeArrayPoolList(static (ref RlpReader c) => c.DecodeKeccak(), limit: limit);

        public void Serialize(IByteBuffer byteBuffer, T message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            ByteBufferRlpWriter writer = new(byteBuffer);

            writer.StartSequence(contentLength);
            ReadOnlySpan<Hash256> hashes = message.Hashes.AsSpan();
            for (int i = 0; i < hashes.Length; i++)
            {
                writer.Encode(hashes[i]);
            }
        }

        public abstract T Deserialize(IByteBuffer byteBuffer);
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
