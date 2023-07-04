// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class HelperTrieProofsMessageSerializer : IZeroMessageSerializer<HelperTrieProofsMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, HelperTrieProofsMessage message)
        {
            Keccak[] proofNodesKeccak = new Keccak[message.ProofNodes.Length];
            int proofNodesContentLength = 0;
            for (int i = 0; i < message.ProofNodes.Length; i++)
            {
                proofNodesKeccak[i] = new Keccak(message.ProofNodes[i]);
                proofNodesContentLength += Rlp.LengthOf(proofNodesKeccak[i]);
            }

            int tempAuxContentLength = 0;
            for (int i = 0; i < message.AuxiliaryData.Length; i++)
            {
                tempAuxContentLength += Rlp.LengthOf(message.AuxiliaryData[i]);
            }

            int innerContentLength = Rlp.LengthOfSequence(proofNodesContentLength) + Rlp.LengthOfSequence(tempAuxContentLength);
            int contentLength =
                Rlp.LengthOf(message.RequestId) +
                Rlp.LengthOf(message.BufferValue) +
                Rlp.LengthOfSequence(innerContentLength);
            int totalLength = Rlp.LengthOfSequence(contentLength);

            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            byteBuffer.EnsureWritable(totalLength);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.Encode(message.BufferValue);
            rlpStream.StartSequence(innerContentLength);
            rlpStream.StartSequence(proofNodesContentLength);
            for (int i = 0; i < message.ProofNodes.Length; i++)
            {
                rlpStream.Encode(proofNodesKeccak[i]);
            }
            rlpStream.StartSequence(tempAuxContentLength);
            for (int i = 0; i < message.AuxiliaryData.Length; i++)
            {
                rlpStream.Encode(message.AuxiliaryData[i]);
            }
        }

        public HelperTrieProofsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
        }

        public HelperTrieProofsMessage Deserialize(RlpStream rlpStream)
        {
            HelperTrieProofsMessage message = new();
            rlpStream.ReadSequenceLength();
            message.RequestId = rlpStream.DecodeLong();
            message.BufferValue = rlpStream.DecodeInt();
            rlpStream.ReadSequenceLength();
            message.ProofNodes = rlpStream.DecodeArray(stream => stream.DecodeByteArray());
            message.AuxiliaryData = rlpStream.DecodeArray(stream => stream.DecodeByteArray());
            return message;
        }
    }
}
