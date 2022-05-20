//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public abstract class SnapSerializerBase<T> : IZeroInnerMessageSerializer<T> where T : MessageBase
    {
        public abstract void Serialize(IByteBuffer byteBuffer, T message);
        protected abstract T Deserialize(RlpStream rlpStream);
        public abstract int GetLength(T message, out int contentLength);

        protected NettyRlpStream GetRlpStreamAndStartSequence(IByteBuffer byteBuffer, T msg)
        {
            int totalLength = GetLength(msg, out int contentLength);
            byteBuffer.EnsureWritable(totalLength, true);
            NettyRlpStream stream = new (byteBuffer);
            stream.StartSequence(contentLength);

            return stream;
        }

        public T Deserialize(IByteBuffer byteBuffer)
        {
            NettyRlpStream rlpStream = new (byteBuffer);
            return Deserialize(rlpStream);
        }
    }
}
