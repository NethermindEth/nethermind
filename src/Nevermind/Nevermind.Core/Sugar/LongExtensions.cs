using System;

namespace Nevermind.Core.Sugar
{
    public static class LongExtensions
    {
        public static byte[] ToBigEndianByteArray(this long value)
        {
            return BitConverter.GetBytes(BitConverter.IsLittleEndian ? Swap(value) : value);
        }

        // https://antonymale.co.uk/converting-endianness-in-csharp.html
        private static ulong Swap(ulong val)
        {
            // Swap adjacent 32-bit blocks
            val = (val >> 32) | (val << 32);
            // Swap adjacent 16-bit blocks
            val = ((val & 0xFFFF0000FFFF0000U) >> 16) | ((val & 0x0000FFFF0000FFFFU) << 16);
            // Swap adjacent 8-bit blocks
            val = ((val & 0xFF00FF00FF00FF00U) >> 8) | ((val & 0x00FF00FF00FF00FFU) << 8);
            return val;
        }

        // https://antonymale.co.uk/converting-endianness-in-csharp.html
        private static long Swap(long val)
        {
            unchecked
            {
                return (long)Swap((ulong)val);
            }
        }
    }
}