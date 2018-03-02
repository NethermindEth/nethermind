using System;

namespace Nethermind.Core.Extensions
{
    public static class UIntExtensions
    {
        public static byte[] ToByteArray(this uint value, Bytes.Endianness endianness)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if(BitConverter.IsLittleEndian && endianness != Bytes.Endianness.Little || !BitConverter.IsLittleEndian && endianness == Bytes.Endianness.Little)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }
        
        public static byte[] ToBigEndianByteArray(this uint value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }
    }
}