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
            NettyRlpStream nettyRlpStream = new(byteBuffer);
            return DeserializeHashes(nettyRlpStream);
        }

        protected static Hash256[] DeserializeHashes(RlpStream rlpStream)
        {
            Hash256[] hashes = rlpStream.DecodeArray(itemContext => itemContext.DecodeKeccak());
            return hashes;
        }

        protected ArrayPoolList<Hash256> DeserializeHashesArrayPool(IByteBuffer byteBuffer)
        {
            NettyRlpStream nettyRlpStream = new(byteBuffer);
            return DeserializeHashesArrayPool(nettyRlpStream);
        }

        protected static ArrayPoolList<Hash256> DeserializeHashesArrayPool(RlpStream rlpStream)
        {
            return rlpStream.DecodeArrayPoolList(itemContext => itemContext.DecodeKeccak());
        }

        public void Serialize(IByteBuffer byteBuffer, T message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);

            rlpStream.StartSequence(contentLength);
            for (int i = 0; i < message.Hashes.Count; i++)
            {
                rlpStream.Encode(message.Hashes[i]);
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
