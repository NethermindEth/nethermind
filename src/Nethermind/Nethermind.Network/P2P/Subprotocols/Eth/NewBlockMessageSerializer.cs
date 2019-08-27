﻿/*
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
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class NewBlockMessageSerializer : IMessageSerializer<NewBlockMessage>, IZeroMessageSerializer<NewBlockMessage>
    {
        private BlockDecoder _blockDecoder = new BlockDecoder();

        public byte[] Serialize(NewBlockMessage message)
        {
            int contentLength = _blockDecoder.GetLength(message.Block, RlpBehaviors.None) + Rlp.LengthOf((UInt256) message.TotalDifficulty);
            int totalLength = Rlp.LengthOfSequence(contentLength);
            RlpStream rlpStream = new RlpStream(totalLength);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.Block);
            rlpStream.Encode(message.TotalDifficulty);
            return rlpStream.Data;
        }

        public NewBlockMessage Deserialize(byte[] bytes)
        {
            RlpStream rlpStream = bytes.AsRlpStream();
            return Deserialize(rlpStream);
        }

        private static NewBlockMessage Deserialize(RlpStream rlpStream)
        {
            NewBlockMessage message = new NewBlockMessage();
            rlpStream.ReadSequenceLength();
            message.Block = Rlp.Decode<Block>(rlpStream);
            message.TotalDifficulty = rlpStream.DecodeUInt256();
            return message;
        }

        public void Serialize(IByteBuffer byteBuffer, NewBlockMessage message)
        {
            int contentLength = _blockDecoder.GetLength(message.Block, RlpBehaviors.None) + Rlp.LengthOf(message.TotalDifficulty);
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);

            int totalLength = Rlp.LengthOfSequence(contentLength);
            byteBuffer.EnsureWritable(totalLength, true);
            
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.Block);
            rlpStream.Encode(message.TotalDifficulty);
        }

        public NewBlockMessage Deserialize(IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }
    }
}