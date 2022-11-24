// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Wit.Messages
{
    public class GetBlockWitnessHashesMessageSerializer : IZeroInnerMessageSerializer<GetBlockWitnessHashesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetBlockWitnessHashesMessage message)
        {
            NettyRlpStream nettyRlpStream = new(byteBuffer);
            int totalLength = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(totalLength, true);
            nettyRlpStream.StartSequence(contentLength);
            nettyRlpStream.Encode(message.RequestId);
            nettyRlpStream.Encode(message.BlockHash);
        }

        public int GetLength(GetBlockWitnessHashesMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId)
                            + (message.BlockHash is null ? 1 : Rlp.LengthOfKeccakRlp);
            return Rlp.LengthOfSequence(contentLength) + Rlp.LengthOf(message.RequestId) + Rlp.LengthOf(message.BlockHash);
        }

        public GetBlockWitnessHashesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            rlpStream.ReadSequenceLength();
            long requestId = rlpStream.DecodeLong();
            var hash = rlpStream.DecodeKeccak();
            return new GetBlockWitnessHashesMessage(requestId, hash);
        }
    }
}
