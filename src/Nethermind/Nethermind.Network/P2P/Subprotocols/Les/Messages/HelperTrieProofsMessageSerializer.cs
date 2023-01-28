// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class HelperTrieProofsMessageSerializer : IZeroMessageSerializer<HelperTrieProofsMessage>
    {
        [Todo(Improve.Refactor, "Rlp.Encode<T>(T[]...) could recurse to handle arbitrary array nesting. Would clean this up a lot.")]
        public void Serialize(IByteBuffer byteBuffer, HelperTrieProofsMessage message)
        {
            Rlp[] proofNodesRlp = new Rlp[message.ProofNodes.Length];
            for (int i = 0; i < message.ProofNodes.Length; i++)
            {
                proofNodesRlp[i] = Rlp.Encode(new Keccak(message.ProofNodes[i]));
            }

            Rlp proofsRlp = Rlp.Encode(proofNodesRlp);

            Rlp[] tempAuxRlp = new Rlp[message.AuxiliaryData.Length];
            for (int i = 0; i < message.AuxiliaryData.Length; i++)
            {
                tempAuxRlp[i] = Rlp.Encode(message.AuxiliaryData[i]);
            }
            Rlp auxRlp = Rlp.Encode(tempAuxRlp);

            int innerContentLength = proofsRlp.Length + auxRlp.Length;

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
            rlpStream.Encode(proofsRlp);
            rlpStream.Encode(auxRlp);
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
