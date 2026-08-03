// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages
{
    public class BlockAccessListsMessageSerializer : IZeroMessageSerializer<BlockAccessListsMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, BlockAccessListsMessage message)
        {
            int listLength = Rlp.LengthOfByteArrayList(message.BlockAccessLists);
            int contentLength = Rlp.LengthOf(message.RequestId) + listLength;
            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));
            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(contentLength);
            writer.Encode(message.RequestId);
            writer.WriteByteArrayList(message.BlockAccessLists);
        }

        public BlockAccessListsMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner? memoryOwner = new(byteBuffer);
            RlpReader ctx = new(memoryOwner.Memory.Span);
            int startPos = ctx.Position;
            RlpByteArrayList? list = null;

            try
            {
                ctx.ReadSequenceLength();
                long requestId = ctx.DecodeLong();

                list = RlpByteArrayList.DecodeList(ref ctx, memoryOwner);
                memoryOwner = null;
                byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startPos));

                return new BlockAccessListsMessage(list) { RequestId = requestId };
            }
            catch
            {
                list?.Dispose();
                memoryOwner?.Dispose();
                throw;
            }
        }
    }
}
