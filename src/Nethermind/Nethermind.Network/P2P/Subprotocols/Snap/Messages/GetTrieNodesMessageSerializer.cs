// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetTrieNodesMessageSerializer : IZeroMessageSerializer<GetTrieNodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, GetTrieNodesMessage message)
        {
            int pathsRlpLen = message.Paths?.RlpLength ?? 1;
            int contentLength = Rlp.LengthOf(message.RequestId)
                + Rlp.LengthOf(message.RootHash)
                + (message.Paths is null || message.Paths.Count == 0
                    ? 1
                    : pathsRlpLen)
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
            else
            {
                stream.Write(message.Paths.RlpSpan);
            }

            stream.Encode(message.Bytes);
        }

        public GetTrieNodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner memoryOwner = new(byteBuffer);
            Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, true);
            int startingPosition = ctx.Position;

            ctx.ReadSequenceLength();
            long requestId = ctx.DecodeLong();
            Hash256? rootHash = ctx.DecodeKeccak();

            RlpItemList paths = RlpItemList.DecodeList(ref ctx, memoryOwner);

            long bytes = ctx.DecodeLong();
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startingPosition));

            return new GetTrieNodesMessage { RequestId = requestId, RootHash = rootHash, Paths = paths, Bytes = bytes };
        }
    }
}
