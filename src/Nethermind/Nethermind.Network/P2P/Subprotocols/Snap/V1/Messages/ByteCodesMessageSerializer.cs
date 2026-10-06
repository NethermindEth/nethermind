// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class ByteCodesMessageSerializer : IZeroMessageSerializer<ByteCodesMessage>
    {
        public void Serialize(Span<byte> buffer, ByteCodesMessage message)
        {
            int codesLength = Rlp.LengthOfByteArrayList(message.Codes);
            int contentLength = Rlp.LengthOf(message.RequestId) + codesLength;
            RlpWriter writer = new(buffer);
            writer.StartSequence(contentLength);
            writer.Encode(message.RequestId);
            writer.WriteByteArrayList(message.Codes);
        }

        public int GetLength(ByteCodesMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId) + Rlp.LengthOfByteArrayList(message.Codes);
            return Rlp.LengthOfSequence(contentLength);
        }

        public ByteCodesMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            PooledBuffer? memoryOwner = PooledBuffer.Rent(data.Length);
            data.CopyTo(memoryOwner.Span);
            RlpReader ctx = new(memoryOwner.Memory.Span);
            RlpByteArrayList? list = null;

            try
            {
                ctx.ReadSequenceLength();
                long requestId = ctx.DecodeLong();

                list = RlpByteArrayList.DecodeList(ref ctx, memoryOwner, SnapMessageLimits.ByteCodesRlpLimit);
                memoryOwner = null;
                consumed = ctx.Position;

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
