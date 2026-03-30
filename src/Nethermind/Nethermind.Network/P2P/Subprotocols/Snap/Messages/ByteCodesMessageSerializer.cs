// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class ByteCodesMessageSerializer : IZeroMessageSerializer<ByteCodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, ByteCodesMessage message)
        {
            int codesLength = Rlp.LengthOfByteArrayList(message.Codes);
            int contentLength = Rlp.LengthOf(message.RequestId) + codesLength;
            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));
            NettyRlpStream rlpStream = new(byteBuffer);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.RequestId);
            rlpStream.WriteByteArrayList(message.Codes);
        }

        public ByteCodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner? memoryOwner = new(byteBuffer);
            Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, true);
            int startPos = ctx.Position;
            RlpByteArrayList? list = null;

            try
            {
                ctx.ReadSequenceLength();
                long requestId = ctx.DecodeLong();

                list = RlpByteArrayList.DecodeList(ref ctx, memoryOwner);
                memoryOwner = null;
                byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startPos));

                return new ByteCodesMessage(list) { RequestId = requestId };
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
