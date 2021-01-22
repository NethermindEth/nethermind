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
using Nethermind.Core.Extensions;

namespace Nethermind.Network
{
    public static class IByteBufferExtensions
    {
        public static byte[] ReadAllBytes(this IByteBuffer buffer)
        {
            byte[] bytes = new byte[buffer.ReadableBytes];
            buffer.ReadBytes(bytes);
            return bytes;
        }

        public static string ReadAllHex(this IByteBuffer buffer)
        {
            byte[] bytes = new byte[buffer.ReadableBytes];
            buffer.ReadBytes(bytes);
            return bytes.ToHexString();
        }

        public static void MakeSpace(this IByteBuffer output, int length, string reason = null)
        {
            if (output.WritableBytes < length)
            {
                if (output.ReaderIndex == output.WriterIndex)
                {
                    output.Clear();
                }
                else
                {
                    output.DiscardReadBytes();
                }

                if (output.WritableBytes < length)
                {
                    output.EnsureWritable(length, true);
                }
            }
        }
    }
}
