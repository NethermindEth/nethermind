// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages
{
    public class NewPooledTransactionHashesMessageSerializer
        : IZeroMessageSerializer<NewPooledTransactionHashesMessage68>
    {
        private static readonly RlpLimit TypesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage68>(nameof(NewPooledTransactionHashesMessage68.Types), NethermindSyncLimits.MaxHashesFetch);
        private static readonly RlpLimit SizesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage68>(nameof(NewPooledTransactionHashesMessage68.Sizes), NethermindSyncLimits.MaxHashesFetch);
        private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage68>(nameof(NewPooledTransactionHashesMessage68.Hashes), NethermindSyncLimits.MaxHashesFetch);

        public NewPooledTransactionHashesMessage68 Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            rlpStream.ReadSequenceLength();
            ArrayPoolList<byte> types = rlpStream.DecodeByteArrayPoolList(TypesRlpLimit);
            ArrayPoolList<int> sizes = rlpStream.DecodeArrayPoolList(static item => item.DecodeInt(), limit: SizesRlpLimit);
            ArrayPoolList<Hash256> hashes = rlpStream.DecodeArrayPoolList(static item => item.DecodeKeccak(), limit: HashesRlpLimit);
            return new NewPooledTransactionHashesMessage68(types, sizes, hashes);
        }

        public void Serialize(IByteBuffer byteBuffer, NewPooledTransactionHashesMessage68 message)
        {
            int sizesLength = 0;
            foreach (int size in message.Sizes.AsSpan())
            {
                sizesLength += Rlp.LengthOf(size);
            }

            int hashesLength = 0;
            foreach (Hash256 hash in message.Hashes.AsSpan())
            {
                hashesLength += Rlp.LengthOf(hash);
            }

            int totalSize = Rlp.LengthOf(message.Types) + Rlp.LengthOfSequence(sizesLength) + Rlp.LengthOfSequence(hashesLength);

            byteBuffer.EnsureWritable(totalSize);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);

            rlpStream.StartSequence(totalSize);
            rlpStream.Encode(message.Types.AsSpan());

            rlpStream.StartSequence(sizesLength);
            foreach (int size in message.Sizes.AsSpan())
            {
                rlpStream.Encode(size);
            }

            rlpStream.StartSequence(hashesLength);
            foreach (Hash256 hash in message.Hashes.AsSpan())
            {
                rlpStream.Encode(hash);
            }
        }
    }
}
