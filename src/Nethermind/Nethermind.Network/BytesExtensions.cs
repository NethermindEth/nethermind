// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network
{
    public static class BytesExtensions
    {
        public static IByteBuffer ToUnpooledByteBuffer(this byte[] bytes)
        {
            IByteBuffer buffer = UnpooledByteBufferAllocator.Default.Buffer(bytes.Length);
            buffer.WriteBytes(bytes);
            return buffer;
        }
    }
}
