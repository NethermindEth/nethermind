// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Wit.Messages
{
    public class BlockWitnessHashesMessageSerializer : IZeroInnerMessageSerializer<BlockWitnessHashesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, BlockWitnessHashesMessage message)
        {
            NettyRlpStream nettyRlpStream = new(byteBuffer);

            int contentLength = GetLength(message, out int totalLength);

            byteBuffer.EnsureWritable(totalLength, true);
            nettyRlpStream.StartSequence(contentLength);
            nettyRlpStream.Encode(message.RequestId);
            if (message.Hashes is null)
            {
                nettyRlpStream.EncodeNullObject();
            }
            else
            {
                int hashesContentLength = message.Hashes?.Length * Rlp.LengthOfKeccakRlp ?? 0;
                nettyRlpStream.StartSequence(hashesContentLength);
                foreach (Keccak keccak in message.Hashes)
                {
                    nettyRlpStream.Encode(keccak);
                }
            }
        }

        public int GetLength(BlockWitnessHashesMessage message, out int contentLength)
        {
            if (message.Hashes is null)
            {
                contentLength = Rlp.OfEmptySequence.Length;
            }
            else
            {
                int hashesContentLength = message.Hashes?.Length * Rlp.LengthOfKeccakRlp ?? 0;
                contentLength = Rlp.LengthOfSequence(hashesContentLength) + Rlp.LengthOf(message.RequestId);

            }
            return Rlp.LengthOfSequence(contentLength);
        }

        public BlockWitnessHashesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            rlpStream.ReadSequenceLength();
            long requestId = rlpStream.DecodeLong();
            int sequenceLength = rlpStream.ReadSequenceLength();
            Keccak[] hashes = new Keccak[sequenceLength / Rlp.LengthOfKeccakRlp];
            for (int i = 0; i < hashes.Length; i++)
            {
                hashes[i] = rlpStream.DecodeKeccak();
            }

            return new BlockWitnessHashesMessage(requestId, hashes);
        }
    }
}
