// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class GetBlockHeadersMessageSerializer : IZeroInnerMessageSerializer<GetBlockHeadersMessage>
    {
        private static readonly RlpLimit StartBlockRlpLimit = RlpLimit.For<GetBlockHeadersMessage>(Hash256.Size, nameof(GetBlockHeadersMessage.StartBlockHash));

        public static GetBlockHeadersMessage Deserialize(ref RlpReader ctx)
        {
            GetBlockHeadersMessage message = new();
            ctx.ReadSequenceLength();
            byte[] startingBytes = ctx.DecodeByteArray(StartBlockRlpLimit);
            if (startingBytes.Length == Hash256.Size)
            {
                message.StartBlockHash = new Hash256(startingBytes);
            }
            else
            {
                message.StartBlockNumber = (long)new UInt256(startingBytes, true);
            }

            message.MaxHeaders = ctx.DecodeUInt();
            message.Skip = ctx.DecodeUInt();
            message.Reverse = ctx.DecodeByte();
            return message;
        }

        public void Serialize(IByteBuffer byteBuffer, GetBlockHeadersMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            ByteBufferRlpWriter writer = new(byteBuffer);

            writer.StartSequence(contentLength);
            if (message.StartBlockHash is null)
            {
                writer.Encode(message.StartBlockNumber);
            }
            else
            {
                writer.Encode(message.StartBlockHash);
            }

            writer.Encode(message.MaxHeaders);
            writer.Encode(message.Skip);
            writer.Encode(message.Reverse);
        }

        public GetBlockHeadersMessage Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(Deserialize);

        public int GetLength(GetBlockHeadersMessage message, out int contentLength)
        {
            contentLength = message.StartBlockHash is null
                ? Rlp.LengthOf(message.StartBlockNumber)
                : Rlp.LengthOf(message.StartBlockHash);
            contentLength += Rlp.LengthOf(message.MaxHeaders);
            contentLength += Rlp.LengthOf(message.Skip);
            contentLength += Rlp.LengthOf(message.Reverse);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
