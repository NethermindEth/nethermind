// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetTrieNodesMessageSerializer : IZeroMessageSerializer<GetTrieNodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetTrieNodesMessage message)
        {
            int pathsRlpLen;

            if (message.Paths is null || message.Paths.Count == 0)
            {
                pathsRlpLen = 1;
            }
            else if (message.Paths is IRlpWrapper rlpWrapper)
            {
                pathsRlpLen = rlpWrapper.RlpLength;
            }
            else
            {
                pathsRlpLen = GetPathsRlpLength(message.Paths);
            }

            int contentLength = Rlp.LengthOf(message.RequestId)
                + Rlp.LengthOf(message.RootHash)
                + pathsRlpLen
                + Rlp.LengthOf(message.Bytes);

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));
            NettyRlpStream stream = new(byteBuffer);

            stream.StartSequence(contentLength);

            stream.Encode(message.RequestId);
            stream.Encode(message.RootHash);

            if (message.Paths is null || message.Paths.Count == 0)
            {
                stream.EncodeNullObject();
            }
            else if (message.Paths is IRlpWrapper wrapper)
            {
                wrapper.Write(stream);
            }
            else
            {
                EncodePaths(stream, message.Paths);
            }

            stream.Encode(message.Bytes);
        }

        private static int GetPathsRlpLength(IReadOnlyList<PathGroup> paths)
        {
            int contentLength = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                byte[][] group = paths[i].Group;
                int groupContentLength = 0;
                for (int j = 0; j < group.Length; j++)
                {
                    groupContentLength += Rlp.LengthOf(group[j]);
                }
                contentLength += Rlp.LengthOfSequence(groupContentLength);
            }
            return Rlp.LengthOfSequence(contentLength);
        }

        private static void EncodePaths(RlpStream stream, IReadOnlyList<PathGroup> paths)
        {
            int contentLength = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                byte[][] group = paths[i].Group;
                int groupContentLength = 0;
                for (int j = 0; j < group.Length; j++)
                {
                    groupContentLength += Rlp.LengthOf(group[j]);
                }
                contentLength += Rlp.LengthOfSequence(groupContentLength);
            }

            stream.StartSequence(contentLength);
            for (int i = 0; i < paths.Count; i++)
            {
                byte[][] group = paths[i].Group;
                int groupContentLength = 0;
                for (int j = 0; j < group.Length; j++)
                {
                    groupContentLength += Rlp.LengthOf(group[j]);
                }
                stream.StartSequence(groupContentLength);
                for (int j = 0; j < group.Length; j++)
                {
                    stream.Encode(group[j]);
                }
            }
        }

        public GetTrieNodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner memoryOwner = new(byteBuffer);
            Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, true);
            int startingPosition = ctx.Position;

            ctx.ReadSequenceLength();
            long requestId = ctx.DecodeLong();
            Hash256? rootHash = ctx.DecodeKeccak();

            RlpPathGroupList paths = new(RlpItemList.DecodeList(ref ctx, memoryOwner));

            long bytes = ctx.DecodeLong();
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startingPosition));

            return new GetTrieNodesMessage { RequestId = requestId, RootHash = rootHash, Paths = paths, Bytes = bytes };
        }
    }
}
