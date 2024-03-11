// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages
{
    public class NewPooledTransactionHashesMessageSerializer
        : IZeroMessageSerializer<NewPooledTransactionHashesMessage68>
    {
        public NewPooledTransactionHashesMessage68 Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            rlpStream.ReadSequenceLength();
            ArrayPoolList<byte> types = rlpStream.DecodeByteArrayPoolList();
            ArrayPoolList<int> sizes = rlpStream.DecodeArrayPoolList(item => item.DecodeInt());
            ArrayPoolList<Hash256> hashes = rlpStream.DecodeArrayPoolList(item => item.DecodeKeccak());
            return new NewPooledTransactionHashesMessage68(types, sizes, hashes);
        }

        public void Serialize(IByteBuffer byteBuffer, NewPooledTransactionHashesMessage68 message)
        {
            int sizesLength = 0;
            for (int i = 0; i < message.Sizes.Count; i++)
            {
                sizesLength += Rlp.LengthOf(message.Sizes[i]);
            }

            int hashesLength = 0;
            for (int i = 0; i < message.Hashes.Count; i++)
            {
                hashesLength += Rlp.LengthOf(message.Hashes[i]);
            }

            int totalSize = Rlp.LengthOf(message.Types) + Rlp.LengthOfSequence(sizesLength) + Rlp.LengthOfSequence(hashesLength);

            byteBuffer.EnsureWritable(totalSize);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);

            rlpStream.StartSequence(totalSize);
            rlpStream.Encode(message.Types);

            rlpStream.StartSequence(sizesLength);
            for (int i = 0; i < message.Sizes.Count; ++i)
            {
                rlpStream.Encode(message.Sizes[i]);
            }

            rlpStream.StartSequence(hashesLength);
            for (int i = 0; i < message.Hashes.Count; ++i)
            {
                rlpStream.Encode(message.Hashes[i]);
            }
        }
    }
}
