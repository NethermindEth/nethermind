// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public abstract class HashesMessageSerializer<T> : IZeroInnerMessageSerializer<T> where T : HashesMessage
    {
        protected Hash256[] DeserializeHashes(IByteBuffer byteBuffer)
        {
            Rlp.ValueDecoderContext ctx = byteBuffer.AsRlpContext();
            Hash256[] hashes = DeserializeHashes(ref ctx);
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
            return hashes;
        }

        protected static Hash256[] DeserializeHashes(ref Rlp.ValueDecoderContext ctx, RlpLimit? limit = null)
        {
            Hash256[] hashes = ctx.DecodeArray(static (ref Rlp.ValueDecoderContext c) => c.DecodeKeccak(), limit: limit);
            return hashes;
        }

        protected ArrayPoolList<Hash256> DeserializeHashesArrayPool(IByteBuffer byteBuffer, RlpLimit? limit = null)
        {
            Rlp.ValueDecoderContext ctx = byteBuffer.AsRlpContext();
            ArrayPoolList<Hash256> result = DeserializeHashesArrayPool(ref ctx, limit);
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
            return result;
        }

        protected static ArrayPoolList<Hash256> DeserializeHashesArrayPool(ref Rlp.ValueDecoderContext ctx, RlpLimit? limit = null)
        {
            return ctx.DecodeArrayPoolList(static (ref Rlp.ValueDecoderContext c) => c.DecodeKeccak(), limit: limit);
        }

        public void Serialize(IByteBuffer byteBuffer, T message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);

            rlpStream.StartSequence(contentLength);
            foreach (Hash256 hash in message.Hashes.AsSpan())
            {
                rlpStream.Encode(hash);
            }
        }

        public abstract T Deserialize(IByteBuffer byteBuffer);
        public int GetLength(T message, out int contentLength)
        {
            contentLength = 0;
            for (int i = 0; i < message.Hashes.Count; i++)
            {
                contentLength += Rlp.LengthOf(message.Hashes[i]);
            }

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
