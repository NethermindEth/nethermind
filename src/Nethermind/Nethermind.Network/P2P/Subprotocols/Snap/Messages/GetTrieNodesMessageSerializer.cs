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
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetTrieNodesMessageSerializer : IZeroMessageSerializer<GetTrieNodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetTrieNodesMessage message)
        {
            (int contentLength, int allPathsLength, int[] pathsLengths)  = CalculateLengths(message);

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
                stream.StartSequence(allPathsLength);

                for (int i = 0; i < message.Paths.Length; i++)
                {
                    PathGroup group = message.Paths[i];

                    stream.StartSequence(pathsLengths[i]);

                    for (int j = 0; j < group.Group.Length; j++)
                    {
                        stream.Encode(group.Group[j]);
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
            message.Paths = stream.DecodeArray(DecodeGroup);
            
            message.Bytes = stream.DecodeLong();

            return message;
        }
        
        private PathGroup DecodeGroup(RlpStream stream)
        {
            PathGroup group = new PathGroup();
            group.Group = stream.DecodeArray(s => stream.DecodeByteArray());

            return group;
        }
        
        private (int contentLength, int allPathsLength, int[] pathsLengths) CalculateLengths(GetTrieNodesMessage message)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.RootHash);
            
            int allPathsLength = 0;
            int[] pathsLengths = new int[message.Paths.Length];

            if (message.Paths == null || message.Paths.Length == 0)
            {
                allPathsLength = 1;
            }
            else
            {
                for (var i = 0; i < message.Paths.Length; i++)
                {
                    PathGroup pathGroup = message.Paths[i];
                    int groupLength = 0;

                    foreach (byte[] path in pathGroup.Group)
                    {
                        groupLength += Rlp.LengthOf(path);
                    }

                    pathsLengths[i] = groupLength;
                    allPathsLength += Rlp.LengthOfSequence(groupLength);
                }
            }

            contentLength += Rlp.LengthOfSequence(allPathsLength);

            contentLength += Rlp.LengthOf(message.Bytes);

            return (contentLength, allPathsLength, pathsLengths);
        }
    }
}
