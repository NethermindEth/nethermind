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
// 

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetTrieNodesMessageSerializer : IZeroMessageSerializer<GetTrieNodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetTrieNodesMessage message)
        {
            int contentLength = CalculateLengths(message);
            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength), true);
            NettyRlpStream stream = new (byteBuffer);
            stream.StartSequence(contentLength);
            
            stream.Encode(message.RequestId);
            stream.Encode(message.RootHash);
            if (message.Paths == null || message.Paths.Length == 0)
            {
                stream.EncodeNullObject();
            }
            else
            {
                stream.StartSequence(message.Paths.RlpLength.Value);
                for (int i = 0; i < message.Paths.Length; i++)
                {
                    var accountPaths = message.Paths.Array[i];
                    stream.StartSequence(accountPaths.RlpLength.Value);
                    for (int j = 0; j < accountPaths.Length; j++)
                    {
                        stream.Encode(accountPaths.Array[j]);
                    }
                }
            }
            
            stream.Encode(message.Bytes);
        }

        public GetTrieNodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            GetTrieNodesMessage message = new();
            NettyRlpStream stream = new (byteBuffer);
            
            stream.ReadSequenceLength();

            message.RequestId = stream.DecodeLong();
            message.RootHash = stream.DecodeKeccak();
            message.Paths = new MeasuredArray<MeasuredArray<byte[]>>(stream.DecodeArray(DecodeAccountPaths));
            
            message.Bytes = stream.DecodeLong();

            return message;
        }
        
        private MeasuredArray<byte[]> DecodeAccountPaths(RlpStream stream)
        {
            byte[][] path = stream.DecodeArray(s => stream.DecodeByteArray());
            
            return new MeasuredArray<byte[]>(path);
        }
        
        private int CalculateLengths(GetTrieNodesMessage message)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.RootHash);
            
            int allPathsLength = 0;
            if (message.Paths != null)
            {
                for (var i = 0; i < message.Paths.Length; i++)
                {
                    int accountPathLength = 0;
                    MeasuredArray<byte[]> accountPaths = message.Paths.Array[i];
                    foreach (byte[] path in accountPaths.Array)
                    {
                        accountPathLength += Rlp.LengthOf(path);
                    }

                    accountPaths.RlpLength = accountPathLength;
                    allPathsLength += Rlp.LengthOfSequence(accountPathLength);
                }

                message.Paths.RlpLength = allPathsLength;
            }

            contentLength += Rlp.LengthOfSequence(allPathsLength);
            contentLength += Rlp.LengthOf(message.Bytes);

            return contentLength;
        }
    }
}
