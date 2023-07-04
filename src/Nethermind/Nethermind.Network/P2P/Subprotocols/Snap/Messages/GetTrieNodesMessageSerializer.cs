// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetTrieNodesMessageSerializer : IZeroMessageSerializer<GetTrieNodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetTrieNodesMessage message)
        {
            (int contentLength, int allPathsLength, int[] pathsLengths) = CalculateLengths(message);

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength), true);
            NettyRlpStream stream = new(byteBuffer);

            stream.StartSequence(contentLength);

            stream.Encode(message.RequestId);
            stream.Encode(message.RootHash);

            if (message.Paths is null || message.Paths.Length == 0)
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
            NettyRlpStream stream = new(byteBuffer);

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

            if (message.Paths is null || message.Paths.Length == 0)
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
