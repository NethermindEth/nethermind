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
        public static GetBlockHeadersMessage Deserialize(ref Rlp.ValueDecoderContext ctx)
        {
            GetBlockHeadersMessage message = new();
            ctx.ReadSequenceLength();
            byte[] startingBytes = ctx.DecodeByteArray();
            if (startingBytes.Length == Hash256.Size)
            {
                message.StartBlockHash = new Hash256(startingBytes);
            }
            else
            {
                message.StartBlockNumber = (long)new UInt256(startingBytes, true);
            }

            message.MaxHeaders = ctx.DecodeInt();
            message.Skip = ctx.DecodeInt();
            message.Reverse = ctx.DecodeByte();
            return message;
        }

        public void Serialize(IByteBuffer byteBuffer, GetBlockHeadersMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);

            rlpStream.StartSequence(contentLength);
            if (message.StartBlockHash is null)
            {
                rlpStream.Encode(message.StartBlockNumber);
            }
            else
            {
                rlpStream.Encode(message.StartBlockHash);
            }

            rlpStream.Encode(message.MaxHeaders);
            rlpStream.Encode(message.Skip);
            rlpStream.Encode(message.Reverse);
        }

        public GetBlockHeadersMessage Deserialize(IByteBuffer byteBuffer)
        {
            Rlp.ValueDecoderContext ctx = byteBuffer.AsRlpContext();
            GetBlockHeadersMessage message = Deserialize(ref ctx);
            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position);
            return message;
        }

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
