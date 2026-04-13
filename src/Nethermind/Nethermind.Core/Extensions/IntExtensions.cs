// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions;

public static class IntExtensions
{
    extension(int @this)
    {
        public string ToHexString()
        {
            return $"0x{@this:x}";
        }

        public UInt256 Ether => (uint)@this * Unit.Ether;
        public UInt256 Wei => (uint)@this * Unit.Wei;
        public UInt256 GWei => (uint)@this * Unit.GWei;

        public byte[] ToBigEndianByteArray()
            => ((uint)@this).ToBigEndianByteArray();

        public byte[] ToLittleEndianByteArray()
            => ((uint)@this).ToLittleEndianByteArray();
    }

    extension(uint @this)
    {
        public byte[] ToBigEndianByteArray()
        {
            byte[] bytes = BitConverter.GetBytes(@this);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }

        public byte[] ToLittleEndianByteArray()
        {
            byte[] bytes = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, @this);
            return bytes;
        }
    }
}
