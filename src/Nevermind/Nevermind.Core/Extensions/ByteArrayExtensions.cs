using System;
using System.Data.HashFunction;

namespace Nevermind.Core.Extensions
{
    public static class ByteArrayExtensions
    {
        private static readonly xxHash XxHash = new xxHash(32);

        public static string ToHex(this byte[] bytes, bool withZeroX = true)
        {
            return Hex.FromBytes(bytes, withZeroX);
        }

        public static byte[] Slice(this byte[] bytes, int startIndex)
        {
            byte[] slice = new byte[bytes.Length - startIndex];
            Buffer.BlockCopy(bytes, startIndex, slice, 0, bytes.Length - startIndex);
            return slice;
        }

        public static byte[] Slice(this byte[] bytes, int startIndex, int length)
        {
            if (length == 1)
            {
                return new[] {bytes[startIndex]};
            }

            byte[] slice = new byte[length];
            Buffer.BlockCopy(bytes, startIndex, slice, 0, length);
            return slice;
        }

        public static byte[] SliceWithZeroPadding(this byte[] bytes, int startIndex, int length)
        {
            if (length == 1)
            {
                return bytes.Length == 0 ? new byte[0] : new[] { bytes[startIndex] };
            }
            
            byte[] slice = new byte[length];
            if (startIndex > bytes.Length - 1)
            {
                return slice;
            }

            Buffer.BlockCopy(bytes, startIndex, slice, 0, Math.Min(bytes.Length - startIndex, length));
            return slice;
        }

        public static int GetXxHashCode(this byte[] bytes)
        {
            return BitConverter.ToInt32(XxHash.ComputeHash(bytes), 0);
        }
    }
}