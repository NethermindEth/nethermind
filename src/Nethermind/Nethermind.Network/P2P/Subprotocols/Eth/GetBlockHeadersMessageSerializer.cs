/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class GetBlockHeadersMessageSerializer : IMessageSerializer<GetBlockHeadersMessage>, IZeroMessageSerializer<GetBlockHeadersMessage>
    {
        public byte[] Serialize(GetBlockHeadersMessage message)
        {
            return Rlp.Encode(
                message.StartingBlockHash == null ? Rlp.Encode(message.StartingBlockNumber) : Rlp.Encode(message.StartingBlockHash),
                Rlp.Encode(message.MaxHeaders),
                Rlp.Encode(message.Skip),
                Rlp.Encode(message.Reverse)
            ).Bytes;
        }

        public GetBlockHeadersMessage Deserialize(byte[] bytes)
        {
            RlpStream rlpStream = bytes.AsRlpStream();
            return Deserialize(rlpStream);
        }

        private static GetBlockHeadersMessage Deserialize(RlpStream rlpStream)
        {
            GetBlockHeadersMessage message = new GetBlockHeadersMessage();
            rlpStream.ReadSequenceLength();
            byte[] startingBytes = rlpStream.DecodeByteArray();
            if (startingBytes.Length == 32)
            {
                message.StartingBlockHash = new Keccak(startingBytes);
            }
            else
            {
                UInt256.CreateFromBigEndian(out UInt256 result, startingBytes);
                message.StartingBlockNumber = (long)result;
            }

            message.MaxHeaders = rlpStream.DecodeInt();
            message.Skip = rlpStream.DecodeInt();
            message.Reverse = rlpStream.DecodeByte();
            return message;
        }

        public void Serialize(IByteBuffer byteBuffer, GetBlockHeadersMessage message)
        {
            byte[] oldWay = Serialize(message);
            byteBuffer.EnsureWritable(oldWay.Length, true);
            byteBuffer.WriteBytes(oldWay);
        }

        public GetBlockHeadersMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }
    }
}