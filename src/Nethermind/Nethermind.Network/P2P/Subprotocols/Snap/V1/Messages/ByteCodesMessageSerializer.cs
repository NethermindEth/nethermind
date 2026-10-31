// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class ByteCodesMessageSerializer : IZeroMessageSerializer<ByteCodesMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, ByteCodesMessage message)
        {
            int codesLength = Rlp.LengthOfByteArrayList(message.Codes);
            int contentLength = Rlp.LengthOf(message.RequestId) + codesLength;
            RlpWriter writer = byteBuffer.StartRlpWrite(Rlp.LengthOfSequence(contentLength));
            RlpSequence sequence = writer.StartSequence(contentLength);
            writer.Encode(message.RequestId);
            writer.WriteByteArrayList(message.Codes);

            writer.EndSequence(sequence);
            byteBuffer.CompleteRlpWrite(in writer, Rlp.LengthOfSequence(contentLength));
        }

        public ByteCodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner? memoryOwner = new(byteBuffer);
            RlpReader outer = new(memoryOwner.Memory.Span);
            RlpByteArrayList? list = null;

            try
            {
                RlpReader ctx = outer.EnterSequence(out int itemLength);
                long requestId = ctx.DecodeLong();

                list = RlpByteArrayList.DecodeList(ref ctx, memoryOwner, SnapMessageLimits.ByteCodesRlpLimit);
                memoryOwner = null;
                outer.ExitSequenceIgnoringRest(ctx, itemLength);
                byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + outer.BytesRead);

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
