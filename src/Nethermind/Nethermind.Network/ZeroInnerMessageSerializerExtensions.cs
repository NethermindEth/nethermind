// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network;

public static class ZeroInnerMessageSerializerExtensions
{
    extension<T>(IZeroInnerMessageSerializer<T> serializer) where T : MessageBase
    {
        /// <summary>Measures <paramref name="message"/> once, then appends it to <paramref name="byteBuffer"/>.</summary>
        public void SerializeWithLengths(IByteBuffer byteBuffer, T message, int stackSize = LengthStack.DefaultStackallocLength)
        {
            LengthStack lengths = new(stackalloc int[Math.Min(stackSize, LengthStack.MaxStackallocLength)]);
            try
            {
                byteBuffer.EnsureWritable(serializer.PopulateLength(message, ref lengths));
                serializer.Serialize(byteBuffer, message, ref lengths);
                Debug.Assert(lengths.Count == 0, "serializer left lengths on the stack");
            }
            finally
            {
                lengths.Dispose();
            }
        }

        public int GetLength(T message, int stackSize = LengthStack.DefaultStackallocLength)
        {
            LengthStack lengths = new(stackalloc int[Math.Min(stackSize, LengthStack.MaxStackallocLength)]);
            try
            {
                return serializer.PopulateLength(message, ref lengths);
            }
            finally
            {
                lengths.Dispose();
            }
        }
    }
}
