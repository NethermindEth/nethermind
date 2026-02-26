// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            if (message.Codes is IRlpWrapper rlpList)
            {
                ReadOnlySpan<byte> rlpSpan = rlpList.RlpSpan;
                int contentLength = rlpSpan.Length + Rlp.LengthOf(message.RequestId);

                byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));

                NettyRlpStream rlpStream = new(byteBuffer);
                rlpStream.StartSequence(contentLength);
                rlpStream.Encode(message.RequestId);
                rlpStream.Write(rlpSpan);
                return;
            }

            {
                (int contentLength, int codesLength) = GetLength(message);
                byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));
                RlpStream rlpStream = new NettyRlpStream(byteBuffer);

                rlpStream.StartSequence(contentLength);
                rlpStream.Encode(message.RequestId);
                rlpStream.StartSequence(codesLength);
                for (int i = 0; i < message.Codes.Count; i++)
                {
                    rlpStream.Encode(message.Codes[i]);
                }
            }
        }

        public ByteCodesMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner memoryOwner = new(byteBuffer);
            Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, true);
            int startPos = ctx.Position;

            ctx.ReadSequenceLength();
            long requestId = ctx.DecodeLong();

            int prefixStart = ctx.Position;
            int innerLength = ctx.ReadSequenceLength();
            int totalLength = (ctx.Position - prefixStart) + innerLength;

            RlpByteArrayList list = new(memoryOwner, memoryOwner.Memory.Slice(prefixStart, totalLength));
            ctx.Position = prefixStart + totalLength;
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startPos));

            return new ByteCodesMessage(list) { RequestId = requestId };
        }

        public static (int contentLength, int codesLength) GetLength(ByteCodesMessage message)
        {
            int codesLength = 0;
            for (int i = 0; i < message.Codes.Count; i++)
            {
                codesLength += Rlp.LengthOf(message.Codes[i]);
            }

            return (Rlp.LengthOfSequence(codesLength) + Rlp.LengthOf(message.RequestId), codesLength);
        }
    }
}
