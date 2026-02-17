// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));
            NettyRlpStream stream = new(byteBuffer);

            stream.StartSequence(contentLength);

            stream.Encode(message.RequestId);
            stream.Encode(message.RootHash);

            stream.StartSequence(allPathsLength);

            for (int i = 0; i < message.Paths.Count; i++)
            {
                PathGroup group = message.Paths[i];

                stream.StartSequence(pathsLengths[i]);

                for (int j = 0; j < group.Group.Length; j++)
                {
                    stream.Encode(group.Group[j]);
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
            message.Paths = stream.DecodeEnsureArrayPoolList(DecodeGroup);
            message.Bytes = stream.DecodeLong();

            return message;
        }

        private PathGroup DecodeGroup(RlpStream stream) =>
            new()
            {
                Group = DecodeNonNullPaths(stream)
            };

        private static byte[][] DecodeNonNullPaths(RlpStream stream)
        {
            byte[][] group = stream.DecodeEnsureArray(s => s.DecodeByteArray());

            for (int i = 0; i < group.Length; i++)
            {
                if (group[i] is null)
                {
                    throw RlpException.NoNullAllowed();
                }
            }

            return group;
        }

        private static (int contentLength, int allPathsLength, int[] pathsLengths) CalculateLengths(GetTrieNodesMessage message)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.RootHash);

            int allPathsLength = 0;
            int[] pathsLengths = new int[message.Paths.Count];

            for (var i = 0; i < message.Paths.Count; i++)
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

            contentLength += Rlp.LengthOfSequence(allPathsLength);

            contentLength += Rlp.LengthOf(message.Bytes);

            return (contentLength, allPathsLength, pathsLengths);
        }
    }
}
