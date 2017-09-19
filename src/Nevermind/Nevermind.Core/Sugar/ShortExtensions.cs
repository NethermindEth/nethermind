using System;

namespace Nevermind.Core.Sugar
{
    public static class ShortExtensions
    {
        public static byte[] ToBigEndianByteArray(this short value)
        {
            return BitConverter.GetBytes(BitConverter.IsLittleEndian ? Swap(value) : value);
        }

        private static ushort Swap(ushort val)
        {
            unchecked
            {
                return (ushort)(((val & 0xFF00U) >> 8) | ((val & 0x00FFU) << 8));
            }
        }

        private static short Swap(short val)
        {
            unchecked
            {
                return (short)Swap((ushort)val);
            }
        }
    }
}