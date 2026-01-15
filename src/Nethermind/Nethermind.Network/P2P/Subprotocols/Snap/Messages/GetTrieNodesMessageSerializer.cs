// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetTrieNodesMessageSerializer : IZeroMessageSerializer<GetTrieNodesMessage>
    {
        private static readonly PathGroup _defaultPathGroup = new() { Group = [] };

        public void Serialize(IByteBuffer byteBuffer, GetTrieNodesMessage message)
        {
            (int contentLength, int allPathsLength, int[] pathsLengths) = CalculateLengths(message);

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));
            NettyRlpStream stream = new(byteBuffer);

            stream.StartSequence(contentLength);

            stream.Encode(message.RequestId);
            stream.Encode(message.RootHash);

            if (message.Paths is null || message.Paths.Count == 0)
            {
                stream.EncodeNullObject();
            }
            else
            {
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
            }

            stream.Encode(message.Bytes);
        }

        /// <summary>
        /// Maximum total paths allowed across all groups.
        /// Prevents nested amplification DOS (e.g., 10K groups × 1K paths = 10M allocations).
        /// </summary>
        private const int MaxTotalPaths = 100_000;

        public GetTrieNodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream stream = new(byteBuffer);

            // Pass 1: Validate nested structure with counting reader
            // CountingRlpReader automatically counts nested elements through DecodeGroup callback
            CountingRlpReader counter = new(stream) { MaxElementsAllowed = MaxTotalPaths };
            DecodeGetTrieNodes(counter, _defaultPathGroup);

            // Pass 2: Actual decode (limits validated, safe to allocate)
            stream.Position = 0;
            return DecodeGetTrieNodes(stream, _defaultPathGroup);
        }

        private static GetTrieNodesMessage DecodeGetTrieNodes(IRlpReader reader, PathGroup defaultPathGroup)
        {
            GetTrieNodesMessage message = new();
            reader.ReadSequenceLength();

            message.RequestId = reader.DecodeLong();
            message.RootHash = reader.DecodeKeccak();
            message.Paths = reader.DecodeArrayPoolList(DecodeGroup, defaultElement: defaultPathGroup);
            message.Bytes = reader.DecodeLong();

            return message;
        }

        private static PathGroup DecodeGroup(IRlpReader stream) =>
            new()
            {
                Group = stream.DecodeArray(static s => s.DecodeByteArray(), defaultElement: [])
            };

        private static (int contentLength, int allPathsLength, int[] pathsLengths) CalculateLengths(GetTrieNodesMessage message)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.RootHash);

            int allPathsLength = 0;
            int[] pathsLengths = new int[message.Paths.Count];

            if (message.Paths is null || message.Paths.Count == 0)
            {
                allPathsLength = 1;
            }
            else
            {
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
            }

            contentLength += Rlp.LengthOfSequence(allPathsLength);

            contentLength += Rlp.LengthOf(message.Bytes);

            return (contentLength, allPathsLength, pathsLengths);
        }
    }
}
