// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class TrieNodesMessageSerializer : IZeroMessageSerializer<TrieNodesMessage>
    {
        /// <summary>
        /// Maximum number of trie nodes allowed in a single message.
        /// Prevents memory DOS attacks from messages with millions of tiny elements.
        /// </summary>
        private const int MaxNodes = 20_000;

        public void Serialize(IByteBuffer byteBuffer, TrieNodesMessage message)
        {
            (int contentLength, int nodesLength) = GetLength(message);

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));

            NettyRlpStream rlpStream = new(byteBuffer);

            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.StartSequence(nodesLength);
            for (int i = 0; i < message.Nodes.Count; i++)
            {
                rlpStream.Encode(message.Nodes[i]);
            }
        }

        public TrieNodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new(byteBuffer);

            // Pass 1: Validate structure with counting reader to prevent memory DOS
            CountingRlpReader counter = new(rlpStream) { MaxElementsAllowed = MaxNodes };
            DecodeTrieNodes(counter);

            // Pass 2: Actual decode (limits validated, safe to allocate)
            rlpStream.Position = 0;
            return DecodeTrieNodes(rlpStream);
        }

        private static TrieNodesMessage DecodeTrieNodes(IRlpReader reader)
        {
            reader.ReadSequenceLength();
            long requestId = reader.DecodeLong();
            IOwnedReadOnlyList<byte[]> result = reader.DecodeArrayPoolList(static stream => stream.DecodeByteArray());
            return new TrieNodesMessage(result) { RequestId = requestId };
        }

        public static (int contentLength, int nodesLength) GetLength(TrieNodesMessage message)
        {
            int nodesLength = 0;
            for (int i = 0; i < message.Nodes.Count; i++)
            {
                nodesLength += Rlp.LengthOf(message.Nodes[i]);
            }

            return (Rlp.LengthOfSequence(nodesLength) + Rlp.LengthOf(message.RequestId), nodesLength);
        }
    }
}
