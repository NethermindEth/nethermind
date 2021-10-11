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

using DotNetty.Buffers;

namespace Nethermind.Network
{
    public static class ZeroMessageSerializerExtensions
    {
        public static byte[] Serialize<T>(this IZeroMessageSerializer<T> serializer, T message) where T : MessageBase
        {
            IByteBuffer byteBuffer = UnpooledByteBufferAllocator.Default.Buffer(64);
            serializer.Serialize(byteBuffer, message);
            return byteBuffer.ReadAllBytes();
        }
        
        public static T Deserialize<T>(this IZeroMessageSerializer<T> serializer, byte[] message) where T : MessageBase
        {
            var buffer = UnpooledByteBufferAllocator.Default.Buffer(message.Length);
            buffer.WriteBytes(message);
            return serializer.Deserialize(buffer);
        }
    }
}
