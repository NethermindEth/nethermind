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
        /// <summary>Populates <paramref name="message"/> as the only item of <paramref name="lengths"/>, clearing it first.</summary>
        /// <returns>The total serialized length of <paramref name="message"/>.</returns>
        public int PopulateLength(T message, ref LengthList lengths)
        {
            LengthListWriter root = lengths.BeginRootContainer();
            serializer.PopulateLength(message, root);
            return root.Length;
        }

        /// <summary>Measures <paramref name="message"/> once, then appends it to <paramref name="byteBuffer"/>.</summary>
        public void SerializeWithLengths(IByteBuffer byteBuffer, T message, int stackSize = LengthList.DefaultStackallocLength)
        {
            LengthList lengths = new(stackalloc int[Math.Min(stackSize, LengthList.MaxStackallocLength)]);
            try
            {
                byteBuffer.EnsureWritable(serializer.PopulateLength(message, ref lengths));
                serializer.Serialize(byteBuffer, message, ref lengths);
                Debug.Assert(lengths.UnreadCount == 0, "serializer left lengths unread");
            }
            finally
            {
                lengths.Dispose();
            }
        }

        public int GetLength(T message, int stackSize = LengthList.DefaultStackallocLength)
        {
            LengthList lengths = new(stackalloc int[Math.Min(stackSize, LengthList.MaxStackallocLength)]);
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
