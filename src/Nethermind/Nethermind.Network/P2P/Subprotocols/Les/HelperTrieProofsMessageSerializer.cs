//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using DotNetty.Buffers;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class HelperTrieProofsMessageSerializer: IZeroMessageSerializer<HelperTrieProofsMessage>
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
                Rlp.GetSequenceRlpLength(innerContentLength);

            int totalLength = Rlp.GetSequenceRlpLength(contentLength);

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
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }

        public HelperTrieProofsMessage Deserialize(RlpStream rlpStream)
        {
            HelperTrieProofsMessage message = new HelperTrieProofsMessage();
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
