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

using DotNetty.Buffers;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class StatusMessageSerializer : IMessageSerializer<StatusMessage>, IZeroMessageSerializer<StatusMessage>
    {
        public byte[] Serialize(StatusMessage message)
        {
            return Rlp.Encode(
                Rlp.Encode(message.ProtocolVersion),
                Rlp.Encode(message.ChainId),
                Rlp.Encode(message.TotalDifficulty),
                Rlp.Encode(message.BestHash),
                Rlp.Encode(message.GenesisHash)
            ).Bytes;
        }

        public StatusMessage Deserialize(byte[] bytes)
        {
            RlpStream rlpStream = bytes.AsRlpStream();
            return Deserialize(rlpStream);
        }

        private static StatusMessage Deserialize(RlpStream rlpStream)
        {
            StatusMessage statusMessage = new StatusMessage();
            rlpStream.ReadSequenceLength();
            statusMessage.ProtocolVersion = rlpStream.DecodeByte();
            statusMessage.ChainId = rlpStream.DecodeUInt256();
            statusMessage.TotalDifficulty = rlpStream.DecodeUInt256();
            statusMessage.BestHash = rlpStream.DecodeKeccak();
            statusMessage.GenesisHash = rlpStream.DecodeKeccak();
            return statusMessage;
        }

        public void Serialize(IByteBuffer byteBuffer, StatusMessage message)
        {
            NettyRlpStream rlpStream = new NettyRlpStream(byteBuffer);
            int contentLength =
                Rlp.LengthOf(message.ProtocolVersion) +
                Rlp.LengthOf(message.ChainId) +
                Rlp.LengthOf(message.TotalDifficulty) +
                Rlp.LengthOf(message.BestHash) +
                Rlp.LengthOf(message.GenesisHash);

            int totalLength = Rlp.LengthOfSequence(contentLength);
            byteBuffer.EnsureWritable(totalLength);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.ProtocolVersion);
            rlpStream.Encode(message.ChainId);
            rlpStream.Encode(message.TotalDifficulty);
            rlpStream.Encode(message.BestHash);
            rlpStream.Encode(message.GenesisHash);
        }

        public StatusMessage Deserialize(IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }
    }
}