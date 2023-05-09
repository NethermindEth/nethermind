// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

using Nethermind.Int256;

namespace Nethermind.Core.Extensions
{
    public static class Int64Extensions
    {
        public static byte[] ToBigEndianByteArrayWithoutLeadingZeros(this long value)
        {
            byte byte6 = (byte)(value >> 8);
            byte byte5 = (byte)(value >> 16);
            byte byte4 = (byte)(value >> 24);
            byte byte3 = (byte)(value >> 32);
            byte byte2 = (byte)(value >> 40);
            byte byte1 = (byte)(value >> 48);
            byte byte0 = (byte)(value >> 56);

            if (byte0 == 0)
            {
                if (byte1 == 0)
                {
                    if (byte2 == 0)
                    {
                        if (byte3 == 0)
                        {
                            if (byte4 == 0)
                            {
                                if (byte5 == 0)
                                {
                                    if (byte6 == 0)
                                    {
                                        byte[] bytes = new byte[1];
                                        bytes[0] = (byte)value;
                                        return bytes;
                                    }
                                    else
                                    {
                                        byte[] bytes = new byte[2];
                                        bytes[1] = (byte)value;
                                        bytes[0] = byte6;
                                        return bytes;
                                    }
                                }
                                else
                                {
                                    byte[] bytes = new byte[3];
                                    bytes[2] = (byte)value;
                                    bytes[1] = byte6;
                                    bytes[0] = byte5;
                                    return bytes;
                                }
                            }
                            else
                            {
                                byte[] bytes = new byte[4];
                                bytes[3] = (byte)value;
                                bytes[2] = byte6;
                                bytes[1] = byte5;
                                bytes[0] = byte4;
                                return bytes;
                            }
                        }
                        else
                        {
                            byte[] bytes = new byte[5];
                            bytes[4] = (byte)value;
                            bytes[3] = byte6;
                            bytes[2] = byte5;
                            bytes[1] = byte4;
                            bytes[0] = byte3;
                            return bytes;
                        }
                    }
                    else
                    {
                        byte[] bytes = new byte[6];
                        bytes[5] = (byte)value;
                        bytes[4] = byte6;
                        bytes[3] = byte5;
                        bytes[2] = byte4;
                        bytes[1] = byte3;
                        bytes[0] = byte2;
                        return bytes;
                    }
                }
                else
                {
                    byte[] bytes = new byte[7];
                    bytes[6] = (byte)value;
                    bytes[5] = byte6;
                    bytes[4] = byte5;
                    bytes[3] = byte4;
                    bytes[2] = byte3;
                    bytes[1] = byte2;
                    bytes[0] = byte1;
                    return bytes;
                }
            }
            else
            {
                byte[] bytes = new byte[8];
                bytes[7] = (byte)value;
                bytes[6] = byte6;
                bytes[5] = byte5;
                bytes[4] = byte4;
                bytes[3] = byte3;
                bytes[2] = byte2;
                bytes[1] = byte1;
                bytes[0] = byte0;
                return bytes;
            }
        }

        public static byte[] ToBigEndianByteArray(this long value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }
        public static void WriteBigEndian(this long value, Span<byte> output)
        {
            BinaryPrimitives.WriteInt64BigEndian(output, value);
        }

        [SkipLocalsInit]
        public static string ToHexString(this long value, bool skipLeadingZeros)
        {
            if (value == UInt256.Zero)
            {
                return "0x";
            }

            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            return bytes.ToHexString(true, skipLeadingZeros, false);
        }

        [SkipLocalsInit]
        public static string ToHexString(this ulong value, bool skipLeadingZeros)
        {
            if (value == UInt256.Zero)
            {
                return "0x";
            }

            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            return bytes.ToHexString(true, skipLeadingZeros, false);
        }

        [SkipLocalsInit]
        public static string ToHexString(this in UInt256 value, bool skipLeadingZeros)
        {
            if (skipLeadingZeros)
            {
                if (value == UInt256.Zero)
                {
                    return "0x";
                }

                if (value == UInt256.One)
                {
                    return "0x1";
                }
            }

            Span<byte> bytes = stackalloc byte[32];
            value.ToBigEndian(bytes);
            return bytes.ToHexString(true, skipLeadingZeros, false);
        }

        public static long ToLongFromBigEndianByteArrayWithoutLeadingZeros(this byte[]? bytes)
        {
            if (bytes is null)
            {
                return 0L;
            }

            long value = 0;
            int length = bytes.Length;

            for (int i = 0; i < length; i++)
            {
                value += (long)bytes[length - 1 - i] << 8 * i;
            }

            return value;
        }
    }
}
