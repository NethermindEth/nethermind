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
        public static GetBlockHeadersMessage Deserialize(RlpStream rlpStream)
        {
            GetBlockHeadersMessage message = new();
            rlpStream.ReadSequenceLength();
            byte[] startingBytes = rlpStream.DecodeByteArray();
            if (startingBytes.Length == 32)
            {
                message.StartBlockHash = new Keccak(startingBytes);
            }
            else
            {
                message.StartBlockNumber = (long)new UInt256(startingBytes, true);
            }

            message.MaxHeaders = rlpStream.DecodeInt();
            message.Skip = rlpStream.DecodeInt();
            message.Reverse = rlpStream.DecodeByte();
            return message;
        }

        public void Serialize(IByteBuffer byteBuffer, GetBlockHeadersMessage message)
        {
            int length = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(length, true);
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
            NettyRlpStream rlpStream = new(byteBuffer);
            return Deserialize(rlpStream);
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
